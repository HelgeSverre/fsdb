module Fsdb.Sql.Expression

open Fsdb.Ast

type Traversal<'state> =
    | Descend of 'state
    | Prune of 'state

let private frameBoundExpressions =
    function
    | BoundPreceding expression
    | BoundFollowing expression -> [ expression ]
    | UnboundedPreceding
    | CurrentRow
    | UnboundedFollowing -> []

/// Expressions evaluated by a window function.
let windowExpressions =
    function
    | WinRowNumber
    | WinRank _
    | WinPercentRank
    | WinCumeDist -> []
    | WinNTile buckets -> [ buckets ]
    | WinLagLead(_, expression, offset, fallback) ->
        expression :: (Option.toList offset @ Option.toList fallback)
    | WinFirstValue expression
    | WinLastValue expression -> [ expression ]
    | WinNthValue(expression, nth) -> [ expression; nth ]
    | WinAggregate(_, arguments) -> arguments

/// Expressions evaluated by an OVER clause, including frame bounds.
let overExpressions =
    function
    | OverName _ -> []
    | OverSpec spec ->
        let frame =
            spec.Frame
            |> Option.map (fun value -> frameBoundExpressions value.Start @ frameBoundExpressions value.End)
            |> Option.defaultValue []

        spec.PartitionBy @ (spec.OrderBy |> List.map fst) @ frame

let children =
    function
    | AssignUserVariable(_, value) -> [ value ]
    | Row values -> values
    | BinOp(_, left, right) -> [ left; right ]
    | Not expression
    | IsNull expression
    | IsNotNull expression
    | IsTrue expression
    | IsFalse expression
    | Distinct expression
    | OrderBy(expression, _)
    | Cast(expression, _)
    | Collate(expression, _) -> [ expression ]
    | Like(value, pattern, _, _)
    | Regexp(value, pattern) -> [ value; pattern ]
    | In(value, candidates) -> value :: candidates
    | InSubquery(value, _)
    | QuantifiedComparison(value, _, _, _) -> [ value ]
    | Between(value, lower, upper) -> [ value; lower; upper ]
    | FuncCall(_, arguments) -> arguments
    | MatchAgainst(_, query, _) -> [ query ]
    | WindowOver(fn, over) -> windowExpressions fn @ overExpressions over
    | Case(subject, branches, fallback) ->
        Option.toList subject
        @ (branches |> List.collect (fun (condition, result) -> [ condition; result ]))
        @ Option.toList fallback
    | Lit _
    | Placeholder _
    | UserVariable _
    | SystemVariable _
    | Col _
    | QualifiedCol _
    | Star _
    | Exists _
    | Subquery _ -> []

let subqueries =
    function
    | Exists select
    | Subquery select
    | InSubquery(_, select)
    | QuantifiedComparison(_, _, _, select) -> [ select ]
    | _ -> []

/// Subqueries embedded anywhere in an expression, in encounter order.
let collectSubqueries expression =
    let rec loop node =
        (children node |> List.collect loop) @ subqueries node

    loop expression

let fold (visit: 'state -> Expr -> Traversal<'state>) (state: 'state) (expression: Expr) : 'state =
    let rec loop current node =
        match visit current node with
        | Prune next -> next
        | Descend next -> children node |> List.fold loop next

    loop state expression

let exists (predicate: Expr -> bool) (expression: Expr) : bool =
    fold
        (fun found node ->
            if found || predicate node then
                Prune true
            else
                Descend false)
        false
        expression

let private fromItemQualifier =
    function
    | FromTable table -> table.Alias |> Option.defaultValue table.Table
    | FromSubquery(_, alias)
    | FromLateral(_, alias)
    | FromJsonTable(_, _, _, alias) -> alias

let hasQualifiedOuterReference (select: SelectStmt) =
    let localQualifiers =
        (select.From |> Option.map fromItemQualifier |> Option.toList)
        @ (select.Joins |> List.map (fun join -> fromItemQualifier join.Table))
        |> List.map _.ToLowerInvariant()
        |> Set.ofList

    let expressions =
        (select.Projections |> List.map fst)
        @ (select.Where |> Option.toList)
        @ (select.Having |> Option.toList)
        @ select.GroupBy
        @ (select.OrderBy |> List.map fst)
        @ (select.Joins |> List.map _.On)

    let referencesUnknownQualifier =
        exists (function
            | QualifiedCol(qualifier, _) -> not (Set.contains (qualifier.ToLowerInvariant()) localQualifiers)
            | _ -> false)

    expressions |> List.exists referencesUnknownQualifier

let collect (chooser: Expr -> 'value option) (expression: Expr) : 'value list =
    fold
        (fun values node ->
            match chooser node with
            | Some value -> Descend(value :: values)
            | None -> Descend values)
        []
        expression
    |> List.rev

let tryPick (chooser: Expr -> 'value option) (expression: Expr) : 'value option =
    let rec loop node =
        match chooser node with
        | Some value -> Some value
        | None -> children node |> List.tryPick loop

    loop expression

let private mapFrameBound mapper =
    function
    | BoundPreceding expression -> BoundPreceding(mapper expression)
    | BoundFollowing expression -> BoundFollowing(mapper expression)
    | bound -> bound

let private mapWindowFunction mapper =
    function
    | WinNTile buckets -> WinNTile(mapper buckets)
    | WinLagLead(lead, expression, offset, fallback) ->
        WinLagLead(lead, mapper expression, Option.map mapper offset, Option.map mapper fallback)
    | WinFirstValue expression -> WinFirstValue(mapper expression)
    | WinLastValue expression -> WinLastValue(mapper expression)
    | WinNthValue(expression, nth) -> WinNthValue(mapper expression, mapper nth)
    | WinAggregate(name, arguments) -> WinAggregate(name, List.map mapper arguments)
    | fn -> fn

let private mapOverClause mapper =
    function
    | OverName _ as over -> over
    | OverSpec spec ->
        OverSpec
            { spec with
                PartitionBy = List.map mapper spec.PartitionBy
                OrderBy = spec.OrderBy |> List.map (fun (expression, direction) -> mapper expression, direction)
                Frame =
                    spec.Frame
                    |> Option.map (fun frame ->
                        { frame with
                            Start = mapFrameBound mapper frame.Start
                            End = mapFrameBound mapper frame.End }) }

let mapChildren (mapper: Expr -> Expr) =
    function
    | AssignUserVariable(variable, value) -> AssignUserVariable(variable, mapper value)
    | Row values -> Row(List.map mapper values)
    | BinOp(operator, left, right) -> BinOp(operator, mapper left, mapper right)
    | Not expression -> Not(mapper expression)
    | IsNull expression -> IsNull(mapper expression)
    | IsNotNull expression -> IsNotNull(mapper expression)
    | IsTrue expression -> IsTrue(mapper expression)
    | IsFalse expression -> IsFalse(mapper expression)
    | Like(value, pattern, caseSensitive, escape) -> Like(mapper value, mapper pattern, caseSensitive, escape)
    | Regexp(value, pattern) -> Regexp(mapper value, mapper pattern)
    | In(value, candidates) -> In(mapper value, List.map mapper candidates)
    | InSubquery(value, select) -> InSubquery(mapper value, select)
    | Between(value, lower, upper) -> Between(mapper value, mapper lower, mapper upper)
    | FuncCall(name, arguments) -> FuncCall(name, List.map mapper arguments)
    | MatchAgainst(columns, query, mode) -> MatchAgainst(columns, mapper query, mode)
    | WindowOver(fn, over) -> WindowOver(mapWindowFunction mapper fn, mapOverClause mapper over)
    | Distinct expression -> Distinct(mapper expression)
    | OrderBy(expression, direction) -> OrderBy(mapper expression, direction)
    | Cast(expression, columnType) -> Cast(mapper expression, columnType)
    | Collate(expression, collation) -> Collate(mapper expression, collation)
    | QuantifiedComparison(value, operator, quantifier, select) ->
        QuantifiedComparison(mapper value, operator, quantifier, select)
    | Case(subject, branches, fallback) ->
        Case(
            Option.map mapper subject,
            branches |> List.map (fun (condition, result) -> mapper condition, mapper result),
            Option.map mapper fallback
        )
    | expression -> expression

let rewrite (replace: Expr -> Expr option) (expression: Expr) : Expr =
    let rec loop node =
        match replace node with
        | Some replacement -> replacement
        | None -> mapChildren loop node

    loop expression

/// Rewrites an expression and every expression inside its subqueries.
let rec rewriteTree (replace: Expr -> Expr option) (expression: Expr) : Expr =
    rewrite
        (fun node ->
            match replace node with
            | Some replacement -> Some replacement
            | None ->
                match node with
                | Exists select -> Some(Exists(rewriteSelect replace select))
                | Subquery select -> Some(Subquery(rewriteSelect replace select))
                | InSubquery(value, select) -> Some(InSubquery(rewriteTree replace value, rewriteSelect replace select))
                | QuantifiedComparison(value, operator, quantifier, select) ->
                    Some(QuantifiedComparison(rewriteTree replace value, operator, quantifier, rewriteSelect replace select))
                | _ -> None)
        expression

and private rewriteWindowSpec replace (spec: WindowSpec) =
    let rewriteBound =
        function
        | BoundPreceding expression -> BoundPreceding(rewriteTree replace expression)
        | BoundFollowing expression -> BoundFollowing(rewriteTree replace expression)
        | bound -> bound

    { spec with
        PartitionBy = List.map (rewriteTree replace) spec.PartitionBy
        OrderBy = spec.OrderBy |> List.map (fun (expression, direction) -> rewriteTree replace expression, direction)
        Frame =
            spec.Frame
            |> Option.map (fun frame ->
                { frame with
                    Start = rewriteBound frame.Start
                    End = rewriteBound frame.End }) }

and private rewriteFromItem replace =
    function
    | FromTable _ as item -> item
    | FromSubquery(select, alias) -> FromSubquery(rewriteSelectOrUnion replace select, alias)
    | FromJsonTable(source, path, columns, alias) ->
        FromJsonTable(rewriteTree replace source, path, columns, alias)
    | FromLateral(select, alias) -> FromLateral(rewriteSelectOrUnion replace select, alias)

and private rewriteJoin replace (join: Join) =
    { join with
        Table = rewriteFromItem replace join.Table
        On = rewriteTree replace join.On }

and private rewriteSelect replace (select: SelectStmt) =
    let rewriteOrderKey (expression, direction) = rewriteTree replace expression, direction

    { select with
        Projections = select.Projections |> List.map (fun (expression, alias) -> rewriteTree replace expression, alias)
        From = Option.map (rewriteFromItem replace) select.From
        Joins = List.map (rewriteJoin replace) select.Joins
        Where = Option.map (rewriteTree replace) select.Where
        GroupBy = List.map (rewriteTree replace) select.GroupBy
        Windows = select.Windows |> List.map (fun (name, spec) -> name, rewriteWindowSpec replace spec)
        Ctes = select.Ctes |> List.map (fun cte -> { cte with Body = rewriteSelectOrUnion replace cte.Body })
        Having = Option.map (rewriteTree replace) select.Having
        OrderBy = List.map rewriteOrderKey select.OrderBy
        Limit = Option.map (rewriteTree replace) select.Limit
        Offset = Option.map (rewriteTree replace) select.Offset }

and private rewriteSelectOrUnion replace =
    function
    | PlainSelect select -> PlainSelect(rewriteSelect replace select)
    | UnionSelect(first, rest, orderBy, limit, offset) ->
        let rewriteOrderKey (expression, direction) = rewriteTree replace expression, direction

        UnionSelect(
            rewriteSelect replace first,
            rest |> List.map (fun (kind, select) -> kind, rewriteSelect replace select),
            List.map rewriteOrderKey orderBy,
            Option.map (rewriteTree replace) limit,
            Option.map (rewriteTree replace) offset
        )

/// Rewrites every executable expression position in a statement.
let rec rewriteStatement replace =
    let rewriteExpression = rewriteTree replace
    let rewriteOrderKey (expression, direction) = rewriteExpression expression, direction
    let rewriteAssignment assignment = { assignment with Value = rewriteExpression assignment.Value }

    function
    | CreateTableAs(name, query, ifNotExists) ->
        CreateTableAs(name, rewriteStatement replace query, ifNotExists)
    | Select select -> Select(rewriteSelect replace select)
    | Do expressions -> Do(List.map rewriteExpression expressions)
    | Union(first, rest, orderBy, limit, offset) ->
        Union(
            rewriteSelect replace first,
            rest |> List.map (fun (kind, select) -> kind, rewriteSelect replace select),
            List.map rewriteOrderKey orderBy,
            Option.map rewriteExpression limit,
            Option.map rewriteExpression offset
        )
    | Insert(table, columns, rows, onDuplicate, ignore) ->
        Insert(
            table,
            columns,
            rows |> List.map (List.map rewriteExpression),
            onDuplicate |> List.map (fun (column, expression) -> column, rewriteExpression expression),
            ignore
        )
    | InsertSelect(table, columns, select, onDuplicate, ignore) ->
        InsertSelect(
            table,
            columns,
            rewriteSelect replace select,
            onDuplicate |> List.map (fun (column, expression) -> column, rewriteExpression expression),
            ignore
        )
    | Replace(table, columns, rows) -> Replace(table, columns, rows |> List.map (List.map rewriteExpression))
    | ReplaceSelect(table, columns, select) -> ReplaceSelect(table, columns, rewriteSelect replace select)
    | ReplaceSet(table, assignments) ->
        ReplaceSet(table, assignments |> List.map (fun (column, expression) -> column, rewriteExpression expression))
    | LoadData load ->
        LoadData
            { load with
                Assignments = load.Assignments |> List.map (fun (column, expression) -> column, rewriteExpression expression) }
    | SetTriggerNew(column, value) -> SetTriggerNew(column, rewriteExpression value)
    | Update update ->
        Update
            { update with
                Ctes = update.Ctes |> List.map (fun cte -> { cte with Body = rewriteSelectOrUnion replace cte.Body })
                Assignments = List.map rewriteAssignment update.Assignments
                Where = Option.map rewriteExpression update.Where
                OrderBy = List.map rewriteOrderKey update.OrderBy
                Joins = List.map (rewriteJoin replace) update.Joins
                Limit = Option.map rewriteExpression update.Limit }
    | Delete delete ->
        Delete
            { delete with
                Ctes = delete.Ctes |> List.map (fun cte -> { cte with Body = rewriteSelectOrUnion replace cte.Body })
                Where = Option.map rewriteExpression delete.Where
                OrderBy = List.map rewriteOrderKey delete.OrderBy
                Joins = List.map (rewriteJoin replace) delete.Joins
                Limit = Option.map rewriteExpression delete.Limit }
    | Explain(format, statement) -> Explain(format, rewriteStatement replace statement)
    | statement -> statement

let statementExists predicate statement =
    let mutable found = false

    statement
    |> rewriteStatement (fun expression ->
        if predicate expression then found <- true
        None)
    |> ignore

    found
