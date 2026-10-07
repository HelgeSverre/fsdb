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
    | RuntimeExpression expression
    | Neg expression
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
    | ApproximateLiteral _
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

let rec private fromItemQualifiers =
    function
    | FromTable table -> [ table.Alias |> Option.defaultValue table.Table ]
    | FromSubquery(_, alias)
    | FromLateral(_, alias)
    | FromJsonTable(_, _, _, alias) -> [ alias ]
    | FromJoinGroup(source, joins) ->
        fromItemQualifiers source @ (joins |> List.collect (fun join -> fromItemQualifiers join.Table))

let hasQualifiedOuterReference (select: SelectStmt) =
    let localQualifiers =
        (select.From |> Option.toList |> List.collect fromItemQualifiers)
        @ (select.Joins |> List.collect (fun join -> fromItemQualifiers join.Table))
        |> List.map _.ToLowerInvariant()
        |> Set.ofList

    let expressions =
        (select.Projections |> List.map _.Expression)
        @ (select.Where |> Option.toList)
        @ (select.Having |> Option.toList)
        @ select.GroupBy
        @ (select.OrderBy |> List.map fst)
        @ (select.Joins |> List.collect Join.conditions)

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
    | RuntimeExpression expression -> RuntimeExpression(mapper expression)
    | Neg expression -> Neg(mapper expression)
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

type private RewriteRules =
    { Replace: Expr -> Expr option
      Projection: Projection -> Projection }

/// Rewrites an expression and every expression inside its subqueries.
let rec private rewriteTreeWith (rules: RewriteRules) (expression: Expr) : Expr =
    rewrite
        (fun node ->
            match rules.Replace node with
            | Some replacement -> Some replacement
            | None ->
                match node with
                | Exists select -> Some(Exists(rewriteSelect rules select))
                | Subquery select -> Some(Subquery(rewriteSelect rules select))
                | InSubquery(value, select) -> Some(InSubquery(rewriteTreeWith rules value, rewriteSelect rules select))
                | QuantifiedComparison(value, operator, quantifier, select) ->
                    Some(QuantifiedComparison(rewriteTreeWith rules value, operator, quantifier, rewriteSelect rules select))
                | _ -> None)
        expression

and private rewriteWindowSpec rules (spec: WindowSpec) =
    let rewriteBound =
        function
        | BoundPreceding expression -> BoundPreceding(rewriteTreeWith rules expression)
        | BoundFollowing expression -> BoundFollowing(rewriteTreeWith rules expression)
        | bound -> bound

    { spec with
        PartitionBy = List.map (rewriteTreeWith rules) spec.PartitionBy
        OrderBy = spec.OrderBy |> List.map (fun (expression, direction) -> rewriteTreeWith rules expression, direction)
        Frame =
            spec.Frame
            |> Option.map (fun frame ->
                { frame with
                    Start = rewriteBound frame.Start
                    End = rewriteBound frame.End }) }

and private rewriteFromItem rules =
    function
    | FromTable _ as item -> item
    | FromJoinGroup(source, joins) -> FromJoinGroup(rewriteFromItem rules source, List.map (rewriteJoin rules) joins)
    | FromSubquery(select, alias) -> FromSubquery(rewriteSelectOrUnion rules select, alias)
    | FromJsonTable(source, path, columns, alias) ->
        FromJsonTable(rewriteTreeWith rules source, path, columns, alias)
    | FromLateral(select, alias) -> FromLateral(rewriteSelectOrUnion rules select, alias)

and private rewriteJoin rules (join: Join) =
    { join with
        Table = rewriteFromItem rules join.Table
        On = rewriteTreeWith rules join.On }

and private rewriteSelect rules (select: SelectStmt) =
    let rewriteOrderKey (expression, direction) = rewriteTreeWith rules expression, direction

    { select with
        Projections =
            select.Projections
            |> List.map (fun original ->
                let projection = rules.Projection original
                { projection with Expression = rewriteTreeWith rules projection.Expression })
        From = Option.map (rewriteFromItem rules) select.From
        Joins = List.map (rewriteJoin rules) select.Joins
        Where = Option.map (rewriteTreeWith rules) select.Where
        GroupBy = List.map (rewriteTreeWith rules) select.GroupBy
        Windows = select.Windows |> List.map (fun (name, spec) -> name, rewriteWindowSpec rules spec)
        Ctes = select.Ctes |> List.map (fun cte -> { cte with Body = rewriteSelectOrUnion rules cte.Body })
        Having = Option.map (rewriteTreeWith rules) select.Having
        OrderBy = List.map rewriteOrderKey select.OrderBy
        Limit = Option.map (rewriteTreeWith rules) select.Limit
        Offset = Option.map (rewriteTreeWith rules) select.Offset }

and private rewriteSelectOrUnion rules =
    function
    | PlainSelect select -> PlainSelect(rewriteSelect rules select)
    | UnionSelect(first, rest, orderBy, limit, offset) ->
        let rewriteOrderKey (expression, direction) = rewriteTreeWith rules expression, direction

        UnionSelect(
            rewriteSelect rules first,
            rest |> List.map (fun (kind, select) -> kind, rewriteSelect rules select),
            List.map rewriteOrderKey orderBy,
            Option.map (rewriteTreeWith rules) limit,
            Option.map (rewriteTreeWith rules) offset
        )

/// Rewrites every executable expression position in a statement.
let rec private rewriteStatementWith rules =
    let rewriteExpression = rewriteTreeWith rules
    let rewriteOrderKey (expression, direction) = rewriteExpression expression, direction
    let rewriteAssignment assignment = { assignment with Value = rewriteExpression assignment.Value }

    function
    | CreateTableAs(name, query, ifNotExists, requestedEngine) ->
        CreateTableAs(name, rewriteStatementWith rules query, ifNotExists, requestedEngine)
    | Select select -> Select(rewriteSelect rules select)
    | Do expressions -> Do(List.map rewriteExpression expressions)
    | SetVariables assignments ->
        SetVariables(assignments |> List.map (SetClause.mapExpression rewriteExpression))
    | Union(first, rest, orderBy, limit, offset) ->
        Union(
            rewriteSelect rules first,
            rest |> List.map (fun (kind, select) -> kind, rewriteSelect rules select),
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
            rewriteSelect rules select,
            onDuplicate |> List.map (fun (column, expression) -> column, rewriteExpression expression),
            ignore
        )
    | Replace(table, columns, rows) -> Replace(table, columns, rows |> List.map (List.map rewriteExpression))
    | ReplaceSelect(table, columns, select) -> ReplaceSelect(table, columns, rewriteSelect rules select)
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
                Ctes = update.Ctes |> List.map (fun cte -> { cte with Body = rewriteSelectOrUnion rules cte.Body })
                Assignments = List.map rewriteAssignment update.Assignments
                Where = Option.map rewriteExpression update.Where
                OrderBy = List.map rewriteOrderKey update.OrderBy
                Joins = List.map (rewriteJoin rules) update.Joins
                Limit = Option.map rewriteExpression update.Limit }
    | Delete delete ->
        Delete
            { delete with
                Ctes = delete.Ctes |> List.map (fun cte -> { cte with Body = rewriteSelectOrUnion rules cte.Body })
                Where = Option.map rewriteExpression delete.Where
                OrderBy = List.map rewriteOrderKey delete.OrderBy
                Joins = List.map (rewriteJoin rules) delete.Joins
                Limit = Option.map rewriteExpression delete.Limit }
    | Explain(format, statement) -> Explain(format, rewriteStatementWith rules statement)
    | statement -> statement

/// Rewrites expressions without changing projection aliases.
let rewriteTree replace expression =
    rewriteTreeWith { Replace = replace; Projection = id } expression

/// Rewrites every expression inside a SELECT, including nested query bodies.
let rewriteSelectExpressions replace select =
    rewriteSelect { Replace = replace; Projection = id } select

/// Rewrites every executable expression position in a statement.
let rewriteStatement replace statement =
    rewriteStatementWith { Replace = replace; Projection = id } statement

/// Names unaliased projections from their original expressions before rewriting.
let rewriteStatementWithProjectionNames projectionName replace statement =
    let retainName (projection: Projection) =
        let alias =
            projection.Alias |> Option.orElseWith (fun () ->
                if projection.SourceName.IsSome then None else projectionName projection.Expression)
        { projection with Alias = alias }
    rewriteStatementWith { Replace = replace; Projection = retainName } statement

/// Maps projections throughout a statement, including nested query bodies.
let mapStatementProjections map statement =
    rewriteStatementWith { Replace = (fun _ -> None); Projection = map } statement

let iterStatement visit statement =
    statement
    |> rewriteStatement (fun expression ->
        visit expression
        None)
    |> ignore

let statementCount predicate statement =
    let mutable count = 0

    statement
    |> iterStatement (fun expression ->
        if predicate expression then
            count <- count + 1)

    count

let statementExists predicate statement =
    statementCount predicate statement > 0
