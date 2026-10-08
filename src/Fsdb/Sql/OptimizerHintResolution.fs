module internal Fsdb.OptimizerHintResolution

open System
open System.Collections.Generic
open Fsdb.Ast
open Fsdb.Sql

/// Contextualization warnings precede variable evaluation and name resolution.
type Diagnostics =
    { Context: (int * int * string) list
      ContextOrder: Map<int, int>
      Resolution: (int * string) list }

let empty = { Context = []; ContextOrder = Map.empty; Resolution = [] }
let private sameName left right = String.Equals(left, right, StringComparison.OrdinalIgnoreCase)
let private quote (name: string) = "`" + name.Replace("`", "``") + "`"

// Query blocks are numbered in parse order; source queries resolve before scalar subqueries.
type private BlockOrder = Numbering | NameResolution | Contextualization

let rec private sourceQueries order = function
    | FromTable _ -> []
    | FromJoinGroup(source, joins) -> sourceQueries order source @ (joins |> List.collect (joinQueries order))
    | FromSubquery(query, _) | FromLateral(query, _) -> queryBlocks order query
    | FromJsonTable(expression, _, _, _) -> expressionQueries order expression
and private joinQueries order (join: Join) = sourceQueries order join.Table @ expressionQueries order join.On
and private expressionQueries order expression = Expression.collectSubqueries expression |> List.collect (selectBlocks order)
and private selectBlocks order (select: SelectStmt) =
    let sources =
        (select.From |> Option.toList |> List.collect (sourceQueries order))
        @ (select.Joins |> List.collect (joinQueries order))
    let projections = select.Projections |> List.collect (_.Expression >> expressionQueries order)
    [ if order <> Contextualization then yield select
      for cte in select.Ctes do yield! queryBlocks order cte.Body
      match order with
      | Numbering | Contextualization -> yield! projections; yield! sources
      | NameResolution -> yield! sources; yield! projections
      for expression in Option.toList select.Where @ select.GroupBy @ Option.toList select.Having do
          yield! expressionQueries order expression
      for _, window in select.Windows do
          for expression in Expression.overExpressions (OverSpec window) do yield! expressionQueries order expression
      for expression in (select.OrderBy |> List.map fst) @ Option.toList select.Limit @ Option.toList select.Offset do
          yield! expressionQueries order expression
      if order = Contextualization then yield select ]
and private queryBlocks order = function
    | PlainSelect select -> selectBlocks order select
    | UnionSelect(first, rest, ordering, limit, offset) ->
        selectBlocks order first @ (rest |> List.collect (snd >> selectBlocks order))
        @ ((ordering |> List.map fst) @ Option.toList limit @ Option.toList offset |> List.collect (expressionQueries order))

let rec private statementBlocks order = function
    | Select select -> selectBlocks order select
    | Union(first, rest, ordering, limit, offset) -> queryBlocks order (UnionSelect(first, rest, ordering, limit, offset))
    | Explain(_, statement) -> statementBlocks order statement
    | _ -> []

type private Target =
    | TableTarget of name: string * table: string * indexes: string list
    | IndexTarget of name: string * table: string * index: string
    | JoinTargets of kind: OptimizerHints.JoinOrderKind * targets: (string * string option) list

type private ConflictTarget =
    | WholeBlock
    | Table of string
    | Index of table: string * index: string

type private Block =
    { Number: int
      Select: SelectStmt
      mutable Name: string option
      Targets: ResizeArray<Target>
      Seen: HashSet<string * ConflictTarget>
      JoinOrders: HashSet<OptimizerHints.JoinOrderKind> }

let private systemBlockName block = sprintf "select#%x" block.Number
let private blockName block = block.Name |> Option.defaultWith (fun () -> systemBlockName block)
let private blockSuffix requested = requested |> Option.map (fun name -> "@" + quote name) |> Option.defaultValue ""
let private family (name: string) = if name.StartsWith("NO_", StringComparison.Ordinal) then name.Substring 3 else name
let private perIndexFamily name = match family name with "MRR" | "ICP" | "RANGE_OPTIMIZATION" -> true | _ -> false

let private resolutionOrder =
    [ "BKA"; "BNL"; "ICP"; "MRR"; "RANGE_OPTIMIZATION"; "MERGE"; "INDEX_MERGE"
      "SKIP_SCAN"; "HASH_JOIN"; "INDEX"; "JOIN_INDEX"; "GROUP_INDEX"; "ORDER_INDEX"
      "DERIVED_CONDITION_PUSHDOWN" ]
    |> List.mapi (fun index name -> name, index)
    |> Map.ofList

let private hintOrder name = Map.tryFind (family name) resolutionOrder |> Option.defaultValue Int32.MaxValue

let private orderedStrategies strategies =
    let order = function "FIRSTMATCH" -> 0 | "LOOSESCAN" -> 1 | "MATERIALIZATION" -> 2 | "DUPSWEEDOUT" -> 3 | _ -> 4
    strategies |> List.distinct |> List.sortBy order

let private renderTableHint name table requested indexes =
    let tableText = table |> Option.map quote |> Option.defaultValue ""
    let indexText = if List.isEmpty indexes then "" else " " + (indexes |> List.map quote |> String.concat ", ")
    sprintf "%s(%s%s %s)" name tableText (blockSuffix requested) indexText

let private renderIndexHint name table requested index =
    sprintf "%s(%s%s %s )" name (quote table) (blockSuffix requested) (quote index)

let private renderJoinHint kind requested targets =
    let tables = targets |> List.map (fun (table, block) -> quote table + blockSuffix block) |> String.concat ","
    sprintf "%s(%s %s)" (OptimizerHints.joinOrderName kind) (blockSuffix requested) tables

let private joinConflict kind (seen: HashSet<OptimizerHints.JoinOrderKind>) =
    match kind with
    | OptimizerHints.JoinOrderKind.Fixed -> seen.Count > 0
    | OptimizerHints.JoinOrderKind.Relative -> seen.Contains OptimizerHints.JoinOrderKind.Fixed
    | _ -> seen.Contains OptimizerHints.JoinOrderKind.Fixed || seen.Contains kind

let private targetWarnings indexesFor (block: Block) =
    let warnings = ResizeArray<int * string>()
    let sources = (Option.toList block.Select.From @ (block.Select.Joins |> List.map _.Table)) |> List.collect FromItem.leaves
    let sourceFor table = sources |> List.tryFind (fun source -> FromItem.tryQualifier source |> Option.exists (sameName table))
    let unresolved name label = warnings.Add(3128, sprintf "Unresolved name %s for %s hint" label name)
    let indexExists source index =
        match source with
        | Some(FromTable table) -> indexesFor table |> Option.defaultValue [] |> List.exists (sameName index)
        | _ -> false
    let tableLabel table = quote table + "@" + quote (blockName block)
    // Native warnings visit each table's hint families before its index children.
    let tableTargets =
        block.Targets |> Seq.choose (function
            | (TableTarget(_, table, _) | IndexTarget(_, table, _)) as target -> Some(table, target)
            | JoinTargets _ -> None)
        |> List.ofSeq
        |> List.groupBy (fun (table, _) -> table.ToLowerInvariant())
    for _, targets in tableTargets do
        let table = targets |> List.head |> fst
        let source = sourceFor table
        let tableHints = targets |> List.choose (function _, TableTarget(name, _, _) -> Some name | _ -> None)
        if source.IsNone then
            for name in tableHints |> List.sortBy hintOrder do unresolved name (tableLabel table)
        let indexes =
            targets |> List.collect (function
                | _, TableTarget(name, _, indexes) -> indexes |> List.map (fun index -> index, name)
                | _, IndexTarget(name, _, index) -> [ index, name ]
                | _ -> [])
            |> List.groupBy (fun (index, _) -> index.ToLowerInvariant())
        for _, hints in indexes do
            let index = hints |> List.head |> fst
            if not (indexExists source index) then
                for _, name in hints |> List.distinctBy (snd >> family) |> List.sortBy (snd >> hintOrder) do
                    unresolved name (tableLabel table + " " + quote index)
    for target in block.Targets do
        match target with
        | JoinTargets(kind, targets) ->
            targets |> List.tryFind (fun (table, requested) ->
                let sameBlock = requested |> Option.forall (fun name -> sameName name (blockName block) || sameName name (systemBlockName block))
                not sameBlock || (sourceFor table).IsNone)
            |> Option.iter (fun (table, requested) ->
                unresolved (OptimizerHints.joinOrderName kind) (quote table + blockSuffix requested))
        | _ -> ()
    List.ofSeq warnings

let resolve options sql (hints: OptimizerHints.Hint list) (indexesFor: TableRef -> string list option) =
    let multipleOwners =
        match hints with
        | [] -> false
        | first :: rest -> rest |> List.exists (fun hint -> hint.Location.KeywordOffset <> first.Location.KeywordOffset)
    let needsResolution = multipleOwners || (hints |> List.exists (fun hint ->
        match hint.Value with
        | OptimizerHints.QueryBlockName _ | OptimizerHints.TableHint _ | OptimizerHints.IndexHint _
        | OptimizerHints.JoinOrderHint _ | OptimizerHints.QueryBlockHint _ -> true
        | _ -> false))
    if not needsResolution then empty
    else
        match Parser.parseQueryBlocksWithOptions options sql with
        | Error _ -> empty
        | Ok(statement, positions) ->
            // WITH attaches CTEs by copying the SELECT record; its projection list retains identity.
            let positionOf (select: SelectStmt) =
                positions |> List.tryPick (fun (position, parsed) ->
                    if Object.ReferenceEquals(select.Projections, parsed.Projections) then Some position else None)
            let orderedBlocks order =
                statementBlocks order statement
                |> List.choose (fun select -> positionOf select |> Option.map (fun position -> position, select))
                |> List.distinctBy fst
            let ordered = orderedBlocks NameResolution
            match ordered with
            | [] -> empty
            | _ ->
                let blocks = orderedBlocks Numbering |> List.mapi (fun index (position, select) ->
                    position,
                    { Number = index + 1; Select = select; Name = None
                      Targets = ResizeArray(); Seen = HashSet(); JoinOrders = HashSet() }) |> Map.ofList
                let context = ResizeArray<int * int * string>()
                let named = Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase)
                let warn offset code text = context.Add(offset, code, text)
                let duplicate offset text = warn offset 3126 ("Hint " + text + " is ignored as conflicting/duplicated")
                let owner (hint: OptimizerHints.Hint) =
                    Map.tryFind (Parser.queryBlockSourceOffset options sql hint.Location.KeywordOffset) blocks
                let hintsByOwner =
                    hints |> List.groupBy (fun hint -> Parser.queryBlockSourceOffset options sql hint.Location.KeywordOffset) |> Map.ofList
                let contextHints =
                    orderedBlocks Contextualization
                    |> List.collect (fun (position, _) -> Map.tryFind position hintsByOwner |> Option.defaultValue [])
                let contextOrder = contextHints |> List.mapi (fun index hint -> hint.Offset, index) |> Map.ofList
                let resolveBlock current requested =
                    match requested with
                    | None -> Ok current
                    | Some name ->
                        match named.TryGetValue name with
                        | true, block -> Ok block
                        | _ ->
                            blocks |> Map.toSeq |> Seq.tryPick (fun (_, block) ->
                                if sameName name (systemBlockName block) then Some block else None)
                            |> function Some block -> Ok block | None -> Error name
                for hint in contextHints do
                    match owner hint with
                    | None -> ()
                    | Some current ->
                        let missingBlocks = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        let inBlock name requested apply =
                            match resolveBlock current requested with
                            | Error missing ->
                                if missingBlocks.Add missing then
                                    warn hint.Offset 3127 (sprintf "Query block name %s is not found for %s hint" (quote missing) name)
                            | Ok block -> apply block
                        let tableTarget (name: string) (table: string option) requested printedBlock (indexes: string list) =
                            inBlock name requested (fun block ->
                                let target = table |> Option.map (fun name -> Table(name.ToLowerInvariant())) |> Option.defaultValue WholeBlock
                                let key = family name, target
                                let inherited = table.IsSome && block.Seen.Contains(family name, WholeBlock)
                                if inherited || not (block.Seen.Add key) then duplicate hint.Offset (renderTableHint name table printedBlock indexes)
                                else table |> Option.iter (fun table -> block.Targets.Add(TableTarget(name, table, indexes))))
                        match hint.Value with
                        | OptimizerHints.QueryBlockName name ->
                            if current.Name.IsSome || named.ContainsKey name then duplicate hint.Offset ("QB_NAME(" + quote name + ")")
                            else
                                current.Name <- Some name
                                named.Add(name, current)
                        | OptimizerHints.TableHint(name, requested, targets) ->
                            if targets.IsEmpty then tableTarget name None requested requested []
                            else
                                for table, block in targets do
                                    tableTarget name (Some table) (requested |> Option.orElse block) block []
                        | OptimizerHints.IndexHint(name, table, requested, indexes) when name = "INDEX_MERGE" && indexes.Length = 1 ->
                            inBlock name requested (fun _ ->
                                warn hint.Offset 3614 ("Invalid number of arguments for hint " + renderTableHint name (Some table) requested indexes))
                        | OptimizerHints.IndexHint(name, table, requested, indexes) when perIndexFamily name && not indexes.IsEmpty ->
                            inBlock name requested (fun block ->
                                let tableKey = table.ToLowerInvariant()
                                for index in indexes |> List.distinctBy _.ToLowerInvariant() do
                                    if block.Seen.Contains(family name, Table tableKey)
                                       || not (block.Seen.Add(family name, Index(tableKey, index.ToLowerInvariant()))) then
                                        duplicate hint.Offset (renderIndexHint name table requested index)
                                    else block.Targets.Add(IndexTarget(name, table, index)))
                        | OptimizerHints.IndexHint(name, table, requested, indexes) -> tableTarget name (Some table) requested requested indexes
                        | OptimizerHints.JoinOrderHint(kind, requested, targets) ->
                            inBlock (OptimizerHints.joinOrderName kind) requested (fun block ->
                                if joinConflict kind block.JoinOrders then duplicate hint.Offset (renderJoinHint kind requested targets)
                                else
                                    block.JoinOrders.Add kind |> ignore
                                    block.Targets.Add(JoinTargets(kind, targets)))
                        | OptimizerHints.QueryBlockHint(name, requested, strategies) ->
                            inBlock name requested (fun block ->
                                if not (block.Seen.Add("SUBQUERY", WholeBlock)) then
                                    let arguments = if strategies.IsEmpty then "" else " " + (orderedStrategies strategies |> String.concat ", ")
                                    duplicate hint.Offset (sprintf "%s(%s %s)" name (blockSuffix requested) arguments))
                        | _ -> ()
                let resolution =
                    ordered |> List.collect (fun (position, _) -> targetWarnings indexesFor blocks.[position])
                { Context = List.ofSeq context; ContextOrder = contextOrder; Resolution = resolution }
