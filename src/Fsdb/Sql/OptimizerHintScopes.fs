/// Query-block instances and lexical CTE scopes used by optimizer hints.
module internal Fsdb.OptimizerHintScopes

open System
open Fsdb.Ast
open Fsdb.Sql

// Source offsets identify definitions; numbers identify distinct CTE references.
type Block =
    { Number: int
      SourceOffset: int option
      Sources: FromItem list }

type Walk =
    { Numbered: Block list
      Context: Block list
      Resolution: Block list }

type private Scope =
    { Parent: Scope option
      Definitions: CommonTableExpr list }

let private empty = { Numbered = []; Context = []; Resolution = [] }
let private combine walks =
    { Numbered = walks |> List.collect _.Numbered
      Context = walks |> List.collect _.Context
      Resolution = walks |> List.collect _.Resolution }

let private sources (select: SelectStmt) = Option.toList select.From @ (select.Joins |> List.map _.Table)

let private extend scope definitions =
    if List.isEmpty definitions then scope else { Parent = Some scope; Definitions = definitions }

let rec private lookup scope name =
    match scope.Definitions |> List.tryFindIndex (fun cte -> cte.CteName.Equals(name, StringComparison.OrdinalIgnoreCase)) with
    | Some index ->
        let cte = scope.Definitions.[index]
        let visible = scope.Definitions |> List.take (index + if cte.Recursive then 1 else 0)
        Some(cte, { scope with Definitions = visible })
    | None -> scope.Parent |> Option.bind (fun parent -> lookup parent name)

let private mutationSource (name: string) =
    let separator = name.IndexOf '.'
    let database, table =
        if separator < 0 then None, name
        else Some(name.Substring(0, separator)), name.Substring(separator + 1)
    FromTable { Database = database; Table = table; Alias = None; Partitions = [] }

let collect positionOf statement =
    let mutable nextNumber = 0
    let allocate offset sources =
        nextNumber <- nextNumber + 1
        { Number = nextNumber; SourceOffset = offset; Sources = sources }
    let rec query scope active = function
        | PlainSelect select -> selectBlock scope active select
        | UnionSelect(first, rest, ordering, limit, offset) ->
            let scope = extend scope first.Ctes
            let branches =
                [ yield selectBlock scope active { first with Ctes = [] }
                  for _, select in rest do yield selectBlock scope active select ]
            // UNION owns an additional post-processing block without a hint location.
            nextNumber <- nextNumber + 1
            combine (branches @ [expressions scope active ((ordering |> List.map fst) @ Option.toList limit @ Option.toList offset)])
    and source scope active = function
        | FromTable table when table.Database.IsNone ->
            match lookup scope table.Table with
            | Some(cte, definitionScope) ->
                if active |> List.exists (fun current -> Object.ReferenceEquals(current, cte)) then
                    // The recursive reference reserves a block but does not instantiate its hints again.
                    nextNumber <- nextNumber + 1
                    empty
                else query definitionScope (cte :: active) cte.Body
            | None -> empty
        | FromTable _ -> empty
        | FromJoinGroup(first, joins) -> combine [ source scope active first; joinBlocks scope active joins ]
        | FromSubquery(body, _) | FromLateral(body, _) -> query scope active body
        | FromJsonTable(value, _, _, _) -> expressions scope active [value]
    and joinBlocks scope active joins =
        joins |> List.collect (fun join -> [source scope active join.Table; expressions scope active [join.On]]) |> combine
    and expressions scope active values =
        values |> List.collect Expression.collectSubqueries |> List.map (selectBlock scope active) |> combine
    and selectBlock scope active (select: SelectStmt) =
        let scope = extend scope select.Ctes
        let block = positionOf select |> Option.map (fun offset -> allocate (Some offset) (sources select))
        let projections = expressions scope active (select.Projections |> List.map _.Expression)
        let from = combine [select.From |> Option.map (source scope active) |> Option.defaultValue empty; joinBlocks scope active select.Joins]
        let remaining =
            expressions scope active
                (Option.toList select.Where @ select.GroupBy @ Option.toList select.Having
                 @ (select.Windows |> List.collect (snd >> OverSpec >> Expression.overExpressions))
                 @ (select.OrderBy |> List.map fst) @ Option.toList select.Limit @ Option.toList select.Offset)
        { Numbered = Option.toList block @ projections.Numbered @ from.Numbered @ remaining.Numbered
          Context = projections.Context @ from.Context @ remaining.Context @ Option.toList block
          Resolution = Option.toList block @ from.Resolution @ projections.Resolution @ remaining.Resolution }
    let scope = { Parent = None; Definitions = [] }
    let mutation ctes from (joins: Join list) values =
        let scope = extend scope ctes
        let block = allocate None (from :: (joins |> List.map _.Table))
        let children = combine [source scope [] from; joinBlocks scope [] joins; expressions scope [] values]
        { Numbered = block :: children.Numbered
          Context = children.Context @ [block]
          Resolution = block :: children.Resolution }
    let insertSelect table select assignments =
        let body = selectBlock scope [] select
        let body =
            match body.Numbered with
            | [] -> body
            | first :: _ ->
                let withTarget block =
                    if block.Number = first.Number then { block with Sources = mutationSource table :: block.Sources } else block
                { Numbered = List.map withTarget body.Numbered
                  Context = List.map withTarget body.Context
                  Resolution = List.map withTarget body.Resolution }
        combine [body; expressions scope [] assignments]
    let rec visit = function
        | Select select -> selectBlock scope [] select
        | Union(first, rest, ordering, limit, offset) -> query scope [] (UnionSelect(first, rest, ordering, limit, offset))
        | Explain(_, inner) -> visit inner
        | Update update ->
            mutation update.Ctes (FromTable update.From) update.Joins
                ((update.Assignments |> List.map _.Value) @ Option.toList update.Where
                 @ (update.OrderBy |> List.map fst) @ Option.toList update.Limit)
        | Delete delete ->
            mutation delete.Ctes (FromTable delete.From) delete.Joins
                (Option.toList delete.Where @ (delete.OrderBy |> List.map fst) @ Option.toList delete.Limit)
        | Insert(table, _, rows, assignments, _) -> mutation [] (mutationSource table) [] (List.concat rows @ (assignments |> List.map snd))
        | Replace(table, _, rows) -> mutation [] (mutationSource table) [] (List.concat rows)
        | ReplaceSet(table, assignments) -> mutation [] (mutationSource table) [] (assignments |> List.map snd)
        | InsertSelect(table, _, select, assignments, _) -> insertSelect table select (assignments |> List.map snd)
        | ReplaceSelect(table, _, select) -> insertSelect table select []
        | _ -> empty
    visit statement
