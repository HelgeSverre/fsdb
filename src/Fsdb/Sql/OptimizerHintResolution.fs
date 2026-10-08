module internal Fsdb.OptimizerHintResolution

open System
open System.Collections.Generic
open Fsdb.Ast
open Fsdb.Sql

/// Contextualization warnings precede variable evaluation and name resolution.
type Diagnostics =
    { Context: (int * int * string) list
      Resolution: (int * string) list }

let private empty = { Context = []; Resolution = [] }
let private sameName left right = String.Equals(left, right, StringComparison.OrdinalIgnoreCase)
let private quote (name: string) = "`" + name.Replace("`", "``") + "`"

// Query blocks are numbered in parse order; source queries resolve before scalar subqueries.
type private BlockOrder = Numbering | NameResolution

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
    [ yield select
      for cte in select.Ctes do yield! queryBlocks order cte.Body
      match order with
      | Numbering -> yield! projections; yield! sources
      | NameResolution -> yield! sources; yield! projections
      for expression in Option.toList select.Where @ select.GroupBy @ Option.toList select.Having do
          yield! expressionQueries order expression
      for _, window in select.Windows do
          for expression in Expression.overExpressions (OverSpec window) do yield! expressionQueries order expression
      for expression in (select.OrderBy |> List.map fst) @ Option.toList select.Limit @ Option.toList select.Offset do
          yield! expressionQueries order expression ]
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
    { Name: string
      Table: string
      Indexes: string list }

type private Block =
    { Number: int
      Select: SelectStmt
      mutable Name: string option
      Targets: ResizeArray<Target>
      Seen: HashSet<string * string option> }

let private blockName block = block.Name |> Option.defaultWith (fun () -> sprintf "select#%d" block.Number)

let resolve options sql (hints: OptimizerHints.Hint list) (indexesFor: TableRef -> string list option) =
    let needsResolution = hints |> List.exists (fun hint ->
        match hint.Value with
        | OptimizerHints.QueryBlockName _ | OptimizerHints.TableHint _ | OptimizerHints.IndexHint _ -> true
        | _ -> false)
    if not needsResolution then empty
    else
        match Parser.parseQueryBlocksWithOptions options sql with
        | Error _ -> empty
        | Ok(statement, positions) ->
            let selects = statementBlocks NameResolution statement
            // WITH attaches CTEs by copying the SELECT record; its projection list retains identity.
            let positionOf (select: SelectStmt) =
                positions |> List.tryPick (fun (position, parsed) ->
                    if Object.ReferenceEquals(select.Projections, parsed.Projections) then Some position else None)
            let ordered = selects |> List.choose (fun select -> positionOf select |> Option.map (fun position -> position, select)) |> List.distinctBy fst
            match ordered with
            | [] -> empty
            | _ ->
                let numbered =
                    statementBlocks Numbering statement
                    |> List.choose (fun select -> positionOf select |> Option.map (fun position -> position, select))
                    |> List.distinctBy fst
                let blocks = numbered |> List.mapi (fun index (position, select) ->
                    position,
                    { Number = index + 1; Select = select; Name = None
                      Targets = ResizeArray(); Seen = HashSet() }) |> Map.ofList
                let context = ResizeArray<int * int * string>()
                let named = Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase)
                let warn offset code text = context.Add(offset, code, text)
                let duplicate offset text = warn offset 3126 ("Hint " + text + " is ignored as conflicting/duplicated")
                for hint in hints do
                    match Map.tryFind (Parser.queryBlockSourceOffset options sql hint.Location.KeywordOffset) blocks, hint.Value with
                    | Some block, OptimizerHints.QueryBlockName name ->
                        if block.Name.IsSome then
                            duplicate hint.Offset ("QB_NAME(" + quote name + ")")
                        else
                            block.Name <- Some name
                            if named.ContainsKey name then duplicate hint.Offset ("QB_NAME(" + quote name + ")")
                            else named.Add(name, block)
                    | _ -> ()
                let resolveBlock current requested =
                    match requested with
                    | None -> Ok current
                    | Some name when current.Name |> Option.exists (sameName name) -> Ok current
                    | Some name ->
                        match named.TryGetValue name with
                        | true, block -> Ok block
                        | _ ->
                            blocks |> Map.toSeq |> Seq.tryPick (fun (_, block) ->
                                if sameName name (sprintf "select#%d" block.Number) then Some block else None)
                            |> function Some block -> Ok block | None -> Error name
                for hint in hints do
                    match Map.tryFind (Parser.queryBlockSourceOffset options sql hint.Location.KeywordOffset) blocks with
                    | None -> ()
                    | Some current ->
                        let missingBlocks = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        let target (name: string) (table: string option) requested (indexes: string list) =
                            match resolveBlock current requested with
                            | Error missing ->
                                if missingBlocks.Add missing then
                                    warn hint.Offset 3127 (sprintf "Query block name %s is not found for %s hint" (quote missing) name)
                            | Ok block ->
                                let family = if name.StartsWith("NO_", StringComparison.Ordinal) then name.Substring 3 else name
                                if not (block.Seen.Add(family, table |> Option.map _.ToLowerInvariant())) then
                                    let tableText = table |> Option.map quote |> Option.defaultValue ""
                                    let blockText = requested |> Option.map (fun name -> "@" + quote name) |> Option.defaultValue ""
                                    let indexText = if indexes.IsEmpty then "" else " " + (indexes |> List.map quote |> String.concat " ")
                                    duplicate hint.Offset (sprintf "%s(%s%s %s)" name tableText blockText indexText)
                                else
                                    table |> Option.iter (fun table ->
                                        block.Targets.Add { Name = name; Table = table; Indexes = indexes })
                        match hint.Value with
                        | OptimizerHints.TableHint(name, requested, targets) ->
                            if targets.IsEmpty then target name None requested []
                            else for table, block in targets do target name (Some table) block []
                        | OptimizerHints.IndexHint(name, table, requested, indexes) -> target name (Some table) requested indexes
                        | _ -> ()
                let resolution = ResizeArray<int * string>()
                for position, _ in ordered do
                    let block = blocks.[position]
                    let sources = (Option.toList block.Select.From @ (block.Select.Joins |> List.map _.Table)) |> List.collect FromItem.leaves
                    for target in block.Targets do
                        let source = sources |> List.tryFind (fun source -> FromItem.tryQualifier source |> Option.exists (sameName target.Table))
                        let label = quote target.Table + "@" + quote (blockName block)
                        let unresolved suffix =
                            resolution.Add(3128, sprintf "Unresolved name %s%s for %s hint" label suffix target.Name)
                        if source.IsNone then unresolved ""
                        let indexes =
                            match source with
                            | Some(FromTable table) -> indexesFor table |> Option.defaultValue []
                            | _ -> []
                        for index in target.Indexes do
                            if not (indexes |> List.exists (sameName index)) then unresolved (" " + quote index)
                { Context = List.ofSeq context; Resolution = List.ofSeq resolution }
