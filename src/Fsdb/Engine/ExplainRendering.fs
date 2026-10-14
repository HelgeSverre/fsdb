/// Formats EXPLAIN plans independently of plan collection and SQL execution.
module internal Fsdb.ExplainRendering

open System.Text.Json
open System.Text.Json.Nodes

/// One row of `EXPLAIN`'s classic 12-column tabular output. `Id`/`Table` are
/// `option` since a few rows render `NULL` there (`UNION RESULT`'s `Id`
/// doesn't belong to any one branch; a from-less `SELECT 1`'s `Table`
/// doesn't name one) — `None` renders `NULL` the same way every other
/// resultset cell already does.
type ExplainKey =
    { Names: string list
      Length: int option }

type ExplainRow =
    { Id: int option
      SelectType: string
      Table: string option
      Type: string option
      Key: ExplainKey option
      Ref: string option
      Rows: uint64 option
      Extra: string list }

type ExplainSubqueryClause =
    | ExplainProjection
    | ExplainWhere
    | ExplainHaving
    | ExplainOrderBy

type ExplainOperation =
    | OrderingOperation
    | DistinctOperation
    | GroupingOperation

type ExplainSubqueryPlacement =
    { Clause: ExplainSubqueryClause
      Operations: ExplainOperation list }

type ExplainUnionBranchSubqueries =
    | AuditedUnionBranch of (ExplainSubqueryPlacement * int) list
    | UnsupportedUnionBranch

type ExplainUnionJsonShape =
    { Materialized: bool
      AllowDerived: bool
      BranchSubqueries: ExplainUnionBranchSubqueries list }

type ExplainJsonShape =
    | FlatJson
    | SelectJson of ExplainSubqueryPlacement option
    | MixedSelectJson of (ExplainSubqueryPlacement * int) list
    | UnionJson of ExplainUnionJsonShape

type ExplainNestedSubqueries =
    | NoNestedSubqueries
    | NestedSubqueries of (ExplainSubqueryPlacement * (bool * ExplainRow list) list) list

let renderJson (shape: ExplainJsonShape) (derivedAliases: Map<int, string>) (rows: ExplainRow list) : string =
    let jsonArray values =
        let array = JsonArray()
        values |> Seq.iter (fun value -> array.Add(value: JsonNode))
        array

    let tableNode (row: ExplainRow) =
        let table = JsonObject()
        row.Table |> Option.iter (fun value -> table["table_name"] <- JsonValue.Create(value))
        row.Type |> Option.iter (fun value -> table["access_type"] <- JsonValue.Create(value))

        row.Key
        |> Option.iter (fun key ->
            table["possible_keys"] <-
                key.Names
                |> Seq.map (fun name -> JsonValue.Create(name) :> JsonNode)
                |> jsonArray

            table["key"] <- JsonValue.Create(String.concat "," key.Names)
            key.Length |> Option.iter (fun value -> table["key_length"] <- JsonValue.Create(string value)))

        row.Ref
        |> Option.iter (fun value -> table["ref"] <- jsonArray [ JsonValue.Create(value) ])

        row.Rows
        |> Option.iter (fun value ->
            table["rows_examined_per_scan"] <- JsonValue.Create(value)
            table["rows_produced_per_join"] <- JsonValue.Create(value))

        row.Type |> Option.iter (fun _ -> table["filtered"] <- JsonValue.Create("100.00"))

        if not row.Extra.IsEmpty then
            table["message"] <- JsonValue.Create(String.concat "; " row.Extra)

        let item = JsonObject()
        item["table"] <- table
        item

    let derivedTableId (table: string) =
        if table.StartsWith("<derived", System.StringComparison.Ordinal) && table.EndsWith(">", System.StringComparison.Ordinal) then
            let digits = table.Substring(8, table.Length - 9)
            match System.Int32.TryParse digits with
            | true, id -> Some id
            | _ -> None
        else
            None

    let rec queryBlock
        (derivedBlocks: Map<int, ExplainRow list>)
        (nestedSubqueries: ExplainNestedSubqueries)
        (blockRows: ExplainRow list)
        =
        let block = JsonObject()

        let subqueryNode (dependent: bool, rows: ExplainRow list) =
            let item = JsonObject()
            item["dependent"] <- JsonValue.Create(dependent)
            item["cacheable"] <- JsonValue.Create(not dependent)
            item["query_block"] <- queryBlock Map.empty NoNestedSubqueries rows
            item :> JsonNode

        let nestedTableNode row =
            let sourceRow =
                match nestedSubqueries with
                | NestedSubqueries placements when placements |> List.exists (fun (placement, _) -> not placement.Operations.IsEmpty) ->
                    { row with
                        Extra = row.Extra |> List.filter (fun extra -> extra <> "Using filesort" && extra <> "Using temporary") }
                | _ -> row

            let item = tableNode sourceRow

            row.Table
            |> Option.bind derivedTableId
            |> Option.bind (fun id -> Map.tryFind id derivedBlocks)
            |> Option.iter (fun derivedRows ->
                let materialized = JsonObject()
                materialized["using_temporary_table"] <- JsonValue.Create(true)
                materialized["dependent"] <- JsonValue.Create(false)
                materialized["cacheable"] <- JsonValue.Create(true)
                materialized["query_block"] <- queryBlock derivedBlocks NoNestedSubqueries derivedRows
                let table = item["table"] :?> JsonObject
                row.Id
                |> Option.bind (fun id -> Map.tryFind id derivedAliases)
                |> Option.iter (fun alias -> table["table_name"] <- JsonValue.Create(alias))
                table["materialized_from_subquery"] <- materialized)

            item

        blockRows
        |> List.tryPick _.Id
        |> Option.iter (fun id -> block["select_id"] <- JsonValue.Create(id))

        match blockRows with
        | [ row ] when row.Table.IsNone && row.Type.IsNone ->
            let message = if row.Extra.IsEmpty then "No tables used" else String.concat "; " row.Extra
            block["message"] <- JsonValue.Create(message)
        | [ row ] ->
            let item = nestedTableNode row
            block["table"] <- item["table"].DeepClone()
        | rows ->
            block["nested_loop"] <- rows |> List.map (nestedTableNode >> fun node -> node :> JsonNode) |> jsonArray

        let attachSubqueries (target: JsonObject) clause subqueries =
            match clause with
            | ExplainProjection ->
                let dependent, independent = subqueries |> List.partition fst
                let materializedSource =
                    blockRows
                    |> List.exists (fun row -> row.Table |> Option.bind derivedTableId |> Option.isSome)
                // MySQL retains independent projections beside a materialized
                // source instead of moving them to optimized-away subqueries.
                let selected = if materializedSource then subqueries else dependent
                let optimized = if materializedSource then [] else independent

                if not optimized.IsEmpty then
                    target["optimized_away_subqueries"] <- optimized |> List.map subqueryNode |> jsonArray

                if not selected.IsEmpty then
                    target["select_list_subqueries"] <- selected |> List.map subqueryNode |> jsonArray
            | ExplainHaving -> target["having_subqueries"] <- subqueries |> List.map subqueryNode |> jsonArray
            | ExplainOrderBy ->
                let dependent, independent = subqueries |> List.partition fst

                if not independent.IsEmpty then
                    target["optimized_away_subqueries"] <- independent |> List.map subqueryNode |> jsonArray

                if not dependent.IsEmpty then
                    target["order_by_subqueries"] <- dependent |> List.map subqueryNode |> jsonArray
            | ExplainWhere -> target["attached_subqueries"] <- subqueries |> List.map subqueryNode |> jsonArray

        match nestedSubqueries with
        | NoNestedSubqueries -> ()
        | NestedSubqueries placements ->
            let active = placements |> List.filter (snd >> List.isEmpty >> not)
            let operationPlacements = active |> List.filter (fun (placement, _) -> not placement.Operations.IsEmpty)
            let attachToTable (placement: ExplainSubqueryPlacement, subqueries) =
                if placement.Clause = ExplainWhere then
                    let table = block["table"] :?> JsonObject
                    attachSubqueries table ExplainWhere subqueries

            active |> List.iter attachToTable
            active
            |> List.iter (fun (placement, subqueries) ->
                if placement.Clause = ExplainProjection && placement.Operations.IsEmpty then
                    attachSubqueries block ExplainProjection subqueries)

            if not operationPlacements.IsEmpty then
                let operations = (operationPlacements |> List.head |> fst).Operations
                let orderSubqueries =
                    operationPlacements
                    |> List.tryPick (fun (placement, subqueries) ->
                        if placement.Clause = ExplainOrderBy then Some subqueries else None)
                    |> Option.defaultValue []
                let hasDependentOrderSubquery = orderSubqueries |> List.exists fst
                let hasIndependentOrderSubquery = not orderSubqueries.IsEmpty && not hasDependentOrderSubquery
                let hasFilesort = blockRows |> List.exists (fun row -> List.contains "Using filesort" row.Extra)
                let hasTemporary = blockRows |> List.exists (fun row -> List.contains "Using temporary" row.Extra)
                let source = JsonObject()
                source["table"] <- block["table"].DeepClone()
                block.Remove("table") |> ignore

                let wrapped =
                    operations
                    |> List.rev
                    |> List.fold (fun (content: JsonObject) operationKind ->
                        let operation = JsonObject()

                        for entry in content do
                            operation[entry.Key] <- entry.Value.DeepClone()

                        // A dependent scalar order key filesorts even when the
                        // traditional parent row omits that flag after grouping.
                        let filesort =
                            match operations, operationKind with
                            | [ OrderingOperation; DistinctOperation ], DistinctOperation
                                when hasIndependentOrderSubquery -> false
                            | [ OrderingOperation; DistinctOperation ], DistinctOperation -> hasFilesort
                            | [ OrderingOperation; DistinctOperation ], OrderingOperation -> false
                            | [ OrderingOperation ], OrderingOperation
                                when hasIndependentOrderSubquery -> false
                            | [ OrderingOperation; GroupingOperation ], OrderingOperation
                                when hasIndependentOrderSubquery -> false
                            | _ ->
                                (operationKind = OrderingOperation
                                 && (hasFilesort || hasDependentOrderSubquery))
                                || (operations.Length = 1 && hasFilesort)

                        operation["using_filesort"] <- JsonValue.Create(filesort)

                        if operationKind <> OrderingOperation && hasTemporary then
                            operation["using_temporary_table"] <- JsonValue.Create(true)

                        for placement, subqueries in operationPlacements do
                            let targetOperation =
                                match placement.Clause with
                                | ExplainProjection -> List.last operations
                                | ExplainHaving -> GroupingOperation
                                | ExplainOrderBy when not (subqueries |> List.exists fst) ->
                                    match operations with
                                    | [ OrderingOperation; GroupingOperation ] -> GroupingOperation
                                    | [ OrderingOperation; DistinctOperation ] -> DistinctOperation
                                    | _ -> OrderingOperation
                                | ExplainOrderBy -> OrderingOperation
                                | ExplainWhere -> invalidOp "WHERE subqueries belong to the source table"

                            if operationKind = targetOperation then
                                attachSubqueries operation placement.Clause subqueries

                        let operationName =
                            match operationKind with
                            | OrderingOperation -> "ordering_operation"
                            | GroupingOperation -> "grouping_operation"
                            | DistinctOperation -> "duplicates_removal"

                        let wrapper = JsonObject()
                        wrapper[operationName] <- operation
                        wrapper)
                        source

                for entry in wrapped do
                    block[entry.Key] <- entry.Value.DeepClone()
        block

    let sorted = rows |> List.sortBy (fun row -> row.Id |> Option.defaultValue System.Int32.MaxValue)
    let branchRows =
        sorted |> List.filter (fun row -> row.SelectType = "PRIMARY" || row.SelectType = "UNION")
    let branches =
        branchRows
        |> List.groupBy _.Id
        |> List.choose (fun (id, grouped) -> id |> Option.map (fun value -> value, grouped))
    let derivedBlocks =
        sorted
        |> List.filter (fun row -> row.SelectType = "DERIVED")
        |> List.choose (fun row -> row.Id |> Option.map (fun id -> id, row))
        |> List.groupBy fst
        |> List.map (fun (id, grouped) -> id, grouped |> List.map snd)
        |> Map.ofList
    let subqueryBlocks =
        sorted
        |> List.filter (fun row -> row.SelectType = "SUBQUERY" || row.SelectType = "DEPENDENT SUBQUERY")
        |> List.choose (fun row -> row.Id |> Option.map (fun id -> id, row))
        |> List.groupBy fst
        |> List.map (fun (id, grouped) -> id, grouped |> List.map snd)
    let branchSubqueries branchId =
        let followingBranch =
            branches
            |> List.map fst
            |> List.filter (fun id -> id > branchId)
            |> List.sort
            |> List.tryHead

        subqueryBlocks
        |> List.choose (fun (id, rows) ->
            if id > branchId && (followingBranch |> Option.forall (fun next -> id < next)) then
                Some(rows.Head.SelectType = "DEPENDENT SUBQUERY", rows)
            else
                None)
    let nestedBranchSubqueries branchId placements =
        match placements with
        | [] -> NoNestedSubqueries
        | _ ->
            let assigned, _ =
                placements
                |> List.mapFold (fun remaining (placement, count) ->
                    let owned, rest = List.splitAt count remaining
                    (placement, owned), rest) (branchSubqueries branchId)
            NestedSubqueries assigned
    let referencedDerivedIds =
        sorted
        |> List.choose (fun row -> row.Table |> Option.bind derivedTableId)
        |> Set.ofList
    let plainBranchShape shape =
        branches.Length = shape.BranchSubqueries.Length
        && (sorted
            |> List.forall (fun row ->
                row.SelectType = "PRIMARY"
                || row.SelectType = "UNION"
                || row.SelectType = "UNION RESULT"
                || row.SelectType = "DERIVED"
                || row.SelectType = "SUBQUERY"
                || row.SelectType = "DEPENDENT SUBQUERY"))
        && (shape.AllowDerived || derivedBlocks.IsEmpty)
        && (derivedBlocks |> Map.keys |> Set.ofSeq) = referencedDerivedIds
        && List.forall2
            (fun (branchId, _) branchShape ->
                match branchShape with
                | AuditedUnionBranch placements ->
                    (placements |> List.sumBy snd) = (branchSubqueries branchId).Length
                | UnsupportedUnionBranch -> false)
            branches
            shape.BranchSubqueries

    let plainSelectShape (placement: ExplainSubqueryPlacement option) =
        match branches, subqueryBlocks with
        | [ (branchId, branchRows) ], _ :: _ when placement.IsSome ->
            (derivedBlocks |> Map.keys |> Set.ofSeq) = referencedDerivedIds
            && (subqueryBlocks |> List.forall (fun (id, _) -> id > branchId))
            && (sorted
                |> List.forall (fun row ->
                    row.SelectType = "PRIMARY"
                    || row.SelectType = "DERIVED"
                    || row.SelectType = "SUBQUERY"
                    || row.SelectType = "DEPENDENT SUBQUERY"))
            && (match placement with
                | Some { Clause = ExplainProjection; Operations = [] } -> true
                | Some { Clause = ExplainOrderBy; Operations = [ OrderingOperation ] }
                | Some { Clause = ExplainOrderBy; Operations = [ OrderingOperation; GroupingOperation ] }
                | Some { Clause = ExplainOrderBy; Operations = [ OrderingOperation; DistinctOperation ] }
                | Some { Clause = ExplainHaving; Operations = [ GroupingOperation ] }
                | Some { Clause = ExplainHaving; Operations = [ OrderingOperation; GroupingOperation ] } ->
                    derivedBlocks.IsEmpty && branchRows.Length = 1 && branchRows.Head.Table.IsSome
                | Some { Clause = ExplainWhere; Operations = [] } ->
                    branchRows.Length = 1 && branchRows.Head.Table.IsSome
                | Some _ ->
                    derivedBlocks.IsEmpty
                    && branchRows.Length = 1
                    && branchRows.Head.Table.IsSome
                    && (subqueryBlocks
                        |> List.forall (fun (_, rows) -> rows.Head.SelectType = "DEPENDENT SUBQUERY"))
                | None -> false)
        | _ -> false

    let plainMixedSelectShape placements =
        match branches with
        | [ (branchId, branchRows) ] ->
            (derivedBlocks |> Map.keys |> Set.ofSeq) = referencedDerivedIds
            && (derivedBlocks.IsEmpty
                || ((placements |> List.map (fun (placement, _) -> placement.Clause)) = [ ExplainProjection; ExplainWhere ]
                    && (placements |> List.forall (fun (placement, _) -> placement.Operations.IsEmpty))))
            && branchRows.Length = 1
            && branchRows.Head.Table.IsSome
            && (subqueryBlocks |> List.forall (fun (id, rows) -> id > branchId && rows.Head.SelectType = "DEPENDENT SUBQUERY"))
            && (sorted
                |> List.forall (fun row ->
                    row.SelectType = "PRIMARY"
                    || row.SelectType = "DERIVED"
                    || row.SelectType = "DEPENDENT SUBQUERY"))
            && (placements |> List.sumBy snd) = subqueryBlocks.Length
        | _ -> false

    let rootBlock =
        match shape with
        | SelectJson(Some placement) when plainSelectShape (Some placement) ->
            let branchId, branchRows = List.head branches
            queryBlock derivedBlocks (NestedSubqueries [ placement, branchSubqueries branchId ]) branchRows
        | MixedSelectJson placements when plainMixedSelectShape placements ->
            let branchId, branchRows = List.head branches
            queryBlock derivedBlocks (nestedBranchSubqueries branchId placements) branchRows
        | UnionJson unionShape when plainBranchShape unionShape ->
            let unionResult = JsonObject()
            unionResult["using_temporary_table"] <- JsonValue.Create(unionShape.Materialized)

            if sorted |> List.exists (fun row -> row.SelectType = "UNION RESULT" && row.Extra = [ "Zero limit" ]) then
                unionResult["message"] <- JsonValue.Create("Zero limit")

            let specifications =
                List.zip branches unionShape.BranchSubqueries
                |> List.map (fun ((branchId, branchRows), branchShape) ->
                    let specification = JsonObject()
                    specification["dependent"] <- JsonValue.Create(false)
                    specification["cacheable"] <- JsonValue.Create(true)
                    let placements =
                        match branchShape with
                        | AuditedUnionBranch placements -> placements
                        | UnsupportedUnionBranch -> invalidOp "Unaudited UNION branch cannot use nested JSON rendering"
                    specification["query_block"] <- queryBlock derivedBlocks (nestedBranchSubqueries branchId placements) branchRows
                    specification :> JsonNode)

            unionResult["query_specifications"] <- jsonArray specifications
            let block = JsonObject()
            block["union_result"] <- unionResult
            block
        | _ -> queryBlock Map.empty NoNestedSubqueries sorted

    let root = JsonObject()
    root["query_block"] <- rootBlock
    let options = JsonSerializerOptions(WriteIndented = true)
    root.ToJsonString(options)
