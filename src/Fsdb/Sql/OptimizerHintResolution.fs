module internal Fsdb.OptimizerHintResolution

open System
open System.Collections.Generic
open Fsdb.Ast
open Fsdb.Sql

/// Contextualization warnings precede variable evaluation and name resolution.
type Diagnostics =
    { Context: (int * int * string) list
      Hints: OptimizerHints.Hint list option
      Resolution: (int * string) list
      Execution: (int * (int * string) list) list }

let empty = { Context = []; Hints = None; Resolution = []; Execution = [] }
let private sameName left right = String.Equals(left, right, StringComparison.OrdinalIgnoreCase)
let private quoteIdentifier ansiQuotes (name: string) =
    let delimiter = if ansiQuotes then "\"" else "`"
    delimiter + name.Replace(delimiter, delimiter + delimiter) + delimiter

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
      Sources: FromItem list
      mutable Name: string option
      Targets: ResizeArray<Target>
      Seen: HashSet<string * ConflictTarget>
      JoinOrders: HashSet<OptimizerHints.JoinOrderKind> }

type private ContextItem = Hint of Block * OptimizerHints.Hint | SyntaxWarning of string

let private systemBlockName block = sprintf "select#%x" block.Number
let private blockName block = block.Name |> Option.defaultWith (fun () -> systemBlockName block)
let private blockSuffix quote requested = requested |> Option.map (fun name -> "@" + quote name) |> Option.defaultValue ""
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

let private renderTableHint quote name table requested indexes =
    let tableText = table |> Option.map quote |> Option.defaultValue ""
    let indexText = if List.isEmpty indexes then "" else " " + (indexes |> List.map quote |> String.concat ", ")
    sprintf "%s(%s%s %s)" name tableText (blockSuffix quote requested) indexText

let private renderIndexHint quote name table requested index =
    sprintf "%s(%s%s %s )" name (quote table) (blockSuffix quote requested) (quote index)

let private renderJoinHint quote kind requested targets =
    let tables = targets |> List.map (fun (table, block) -> quote table + blockSuffix quote block) |> String.concat ","
    sprintf "%s(%s %s)" (OptimizerHints.joinOrderName kind) (blockSuffix quote requested) tables

let private joinConflict kind (seen: HashSet<OptimizerHints.JoinOrderKind>) =
    match kind with
    | OptimizerHints.JoinOrderKind.Fixed -> seen.Count > 0
    | OptimizerHints.JoinOrderKind.Relative -> seen.Contains OptimizerHints.JoinOrderKind.Fixed
    | _ -> seen.Contains OptimizerHints.JoinOrderKind.Fixed || seen.Contains kind

let private targetWarnings quote indexesFor (block: Block) =
    let warnings = ResizeArray<int * string>()
    let sources = block.Sources |> List.collect FromItem.leaves
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
    List.ofSeq warnings

let private joinTargetWarnings quote (block: Block) =
    let sources =
        block.Sources |> List.collect FromItem.leaves
        |> List.filter (function
            | FromTable table when table.Database.IsNone && sameName table.Table "dual" -> false
            | _ -> true)
    // MySQL skips join-order target resolution when the query block has no table sources.
    if sources.IsEmpty then []
    else
        block.Targets |> Seq.choose (function
            | JoinTargets(kind, targets) ->
                targets |> List.tryFind (fun (table, requested) ->
                    let sameBlock = requested |> Option.forall (fun name -> sameName name (blockName block) || sameName name (systemBlockName block))
                    let found = sources |> List.exists (fun source -> FromItem.tryQualifier source |> Option.exists (sameName table))
                    not sameBlock || not found)
                |> Option.map (fun (table, requested) ->
                    3128, sprintf "Unresolved name %s for %s hint" (quote table + blockSuffix quote requested) (OptimizerHints.joinOrderName kind))
            | _ -> None)
        |> List.ofSeq

let resolve (options: Parser.ParserOptions) sql hasSyntaxDiagnostics (hints: OptimizerHints.Hint list) (indexesFor: TableRef -> string list option) =
    let quote = quoteIdentifier options.AnsiQuotes
    let multipleOwners =
        match hints with
        | [] -> false
        | first :: rest -> rest |> List.exists (fun hint -> hint.Location.KeywordOffset <> first.Location.KeywordOffset)
    let needsResolution = hasSyntaxDiagnostics || multipleOwners || (hints |> List.exists (fun hint ->
        match hint.Value with
        | OptimizerHints.QueryBlockName _ | OptimizerHints.TableHint _ | OptimizerHints.IndexHint _
        | OptimizerHints.JoinOrderHint _ | OptimizerHints.QueryBlockHint _ -> true
        | OptimizerHints.Timeout _ | OptimizerHints.SetVariable _ when not hint.Location.IsLeadingSelect -> true
        | OptimizerHints.Timeout _ | OptimizerHints.SetVariable _ -> not (hint.Location.StatementKeyword.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
        | _ -> false))
    if not needsResolution then empty
    else
        match Parser.parseQueryBlocksWithOptions options sql with
        | Error _ -> empty
        | Ok(statement, positions, cteBodies) ->
            // WITH attaches CTEs by copying the SELECT record; its projection list retains identity.
            let positionOf (select: SelectStmt) =
                positions |> List.tryPick (fun (position, parsed) ->
                    if Object.ReferenceEquals(select.Projections, parsed.Projections) then Some position else None)
            let scopes = OptimizerHintScopes.collect (OptimizerHintScopes.SourceOffsets positionOf) statement
            match scopes.Numbered with
            | [] -> empty
            | root :: _ ->
                let blocks = scopes.Numbered |> List.map (fun scope ->
                    scope.Number,
                    { Number = scope.Number; Sources = scope.Sources; Name = None
                      Targets = ResizeArray(); Seen = HashSet(); JoinOrders = HashSet() }) |> Map.ofList
                let context = ResizeArray<int * int * string>()
                let named = Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase)
                let warn offset code text = context.Add(offset, code, text)
                let duplicate offset text = warn offset 3126 ("Hint " + text + " is ignored as conflicting/duplicated")
                let selectHints, statementHints =
                    hints |> List.partition (fun hint -> hint.Location.Keyword.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                let hintsBySource =
                    selectHints
                    |> List.groupBy (fun hint -> Parser.queryBlockSourceOffset options sql hint.Location.KeywordOffset)
                    |> Map.ofList
                let contextItems =
                    [ for step in scopes.Context do
                          match step with
                          | OptimizerHintScopes.BlockHints scope ->
                              let definitions = scope.SourceOffset |> Option.bind (fun offset -> Map.tryFind offset hintsBySource) |> Option.defaultValue []
                              for hint in definitions do yield Hint(blocks.[scope.Number], hint)
                          | OptimizerHintScopes.Reparse cte when hasSyntaxDiagnostics ->
                              let body = cteBodies |> List.tryPick (fun (definition, text) -> if Object.ReferenceEquals(definition, cte) then Some text else None)
                              for text in Option.toList body do
                                  yield! OptimizerHints.syntaxDiagnostics options text |> List.map SyntaxWarning
                          | OptimizerHintScopes.Reparse _ -> ()
                      for hint in statementHints do yield Hint(blocks.[root.Number], hint) ]
                let contextHints =
                    [ for index, item in contextItems |> List.indexed do
                          match item with
                          | SyntaxWarning message -> warn index 1064 message
                          | Hint(block, hint) -> yield block, { hint with ContextOrder = Some index } ]
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
                for current, hint in contextHints do
                    let missingBlocks = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    let inBlock name requested apply =
                        match resolveBlock current requested with
                        | Error missing ->
                            if missingBlocks.Add missing then
                                warn (OptimizerHints.contextOrder hint) 3127 (sprintf "Query block name %s is not found for %s hint" (quote missing) name)
                        | Ok block -> apply block
                    let tableTarget (name: string) (table: string option) requested printedBlock (indexes: string list) =
                        inBlock name requested (fun block ->
                            let target = table |> Option.map (fun name -> Table(name.ToLowerInvariant())) |> Option.defaultValue WholeBlock
                            let key = family name, target
                            let inherited = table.IsSome && block.Seen.Contains(family name, WholeBlock)
                            if inherited || not (block.Seen.Add key) then duplicate (OptimizerHints.contextOrder hint) (renderTableHint quote name table printedBlock indexes)
                            else table |> Option.iter (fun table -> block.Targets.Add(TableTarget(name, table, indexes))))
                    match hint.Value with
                    | OptimizerHints.QueryBlockName name ->
                        if current.Name.IsSome || named.ContainsKey name then duplicate (OptimizerHints.contextOrder hint) ("QB_NAME(" + quote name + ")")
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
                            warn (OptimizerHints.contextOrder hint) 3614 ("Invalid number of arguments for hint " + renderTableHint quote name (Some table) requested indexes))
                    | OptimizerHints.IndexHint(name, table, requested, indexes) when perIndexFamily name && not indexes.IsEmpty ->
                        inBlock name requested (fun block ->
                            let tableKey = table.ToLowerInvariant()
                            for index in indexes |> List.distinctBy _.ToLowerInvariant() do
                                if block.Seen.Contains(family name, Table tableKey)
                                   || not (block.Seen.Add(family name, Index(tableKey, index.ToLowerInvariant()))) then
                                    duplicate (OptimizerHints.contextOrder hint) (renderIndexHint quote name table requested index)
                                else block.Targets.Add(IndexTarget(name, table, index)))
                    | OptimizerHints.IndexHint(name, table, requested, indexes) -> tableTarget name (Some table) requested requested indexes
                    | OptimizerHints.JoinOrderHint(kind, requested, targets) ->
                        inBlock (OptimizerHints.joinOrderName kind) requested (fun block ->
                            if joinConflict kind block.JoinOrders then duplicate (OptimizerHints.contextOrder hint) (renderJoinHint quote kind requested targets)
                            else
                                block.JoinOrders.Add kind |> ignore
                                block.Targets.Add(JoinTargets(kind, targets)))
                    | OptimizerHints.QueryBlockHint(name, requested, strategies) ->
                        inBlock name requested (fun block ->
                            if not (block.Seen.Add("SUBQUERY", WholeBlock)) then
                                let arguments = if strategies.IsEmpty then "" else " " + (orderedStrategies strategies |> String.concat ", ")
                                duplicate (OptimizerHints.contextOrder hint) (sprintf "%s(%s %s)" name (blockSuffix quote requested) arguments))
                    | _ -> ()
                let resolution =
                    scopes.Resolution |> List.collect (fun scope -> targetWarnings quote indexesFor blocks.[scope.Number])
                let execution =
                    scopes.Resolution |> List.choose (fun scope ->
                        match joinTargetWarnings quote blocks.[scope.Number] with
                        | [] -> None
                        | warnings -> Some(scope.Number, warnings))
                { Context = List.ofSeq context; Hints = Some(contextHints |> List.map snd)
                  Resolution = resolution; Execution = execution }
