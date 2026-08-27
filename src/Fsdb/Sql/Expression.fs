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

let private windowFunctionExpressions =
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

let private overClauseExpressions =
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
    | WindowOver(fn, over) -> windowFunctionExpressions fn @ overClauseExpressions over
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
