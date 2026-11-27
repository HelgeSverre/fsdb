module Fsdb.Sql.Expression

open Fsdb.Ast

/// A wrapper that supplies a collation independently of its operand.
let (|CollationOverride|_|) = function
    | Collate(value, name) -> Some(value, name)
    | BinaryCast value -> Some(value, "binary")
    | _ -> None

let (|NamedConstant|_|) = function
    | FuncCall(name, [ LiteralValue value; _ ]) when name.Equals("NAME_CONST", System.StringComparison.OrdinalIgnoreCase) ->
        let text =
            match value with
            | Fsdb.Value.VBytes bytes | Fsdb.Value.VBinaryLiteral bytes -> Some(System.Text.Encoding.UTF8.GetString bytes)
            | _ -> Fsdb.Value.toText value
        text |> Option.map Projection.literalName
    | _ -> None

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
    | CastCharset(expression, _, _)
    | CastCharAlias(expression, _, _)
    | CastCharTarget(expression, _, _)
    | CastAsBinary(expression, _)
    | BinaryCast expression
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
    | IntroducedLiteral _
    | ConnectionLiteral _
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

/// Retain declarations reachable from the query under its lexical WITH scopes.
/// A nested binding shadows an outer name without making the outer CTE live.
let internal pruneUnusedTopLevelCtes (select: SelectStmt) =
    let nameKey (name: string) = name.ToLowerInvariant()
    let names = select.Ctes |> List.map (_.CteName >> nameKey) |> Set.ofList
    if select.Ctes.IsEmpty || names.Count <> select.Ctes.Length then select
    else
        let rec extend markRoots scope (declarations: CommonTableExpr list) =
            (scope, declarations)
            ||> List.fold (fun previous declaration ->
                let name = nameKey declaration.CteName
                let visible = if declaration.Recursive then Map.remove name previous else previous
                let dependencies =
                    lazy
                        (let uses = query visible declaration.Body
                         if markRoots then Set.add name uses else uses)
                Map.add name dependencies previous)

        and query scope = function
            | PlainSelect block -> selectUses (extend false scope block.Ctes) block
            | UnionSelect(first, rest, orderBy, limit, offset) ->
                let shared = extend false scope first.Ctes
                Set.unionMany
                    [ yield selectUses shared first
                      for _, branch in rest do
                          yield selectUses (extend false shared branch.Ctes) branch
                      yield expressions shared (List.map fst orderBy @ Option.toList limit @ Option.toList offset) ]

        and selectUses scope (block: SelectStmt) =
            let values =
                (block.Projections |> List.map _.Expression)
                @ (block.Joins |> List.collect Join.conditions)
                @ Option.toList block.Where @ block.GroupBy @ Option.toList block.Having
                @ (block.OrderBy |> List.map fst) @ Option.toList block.Limit @ Option.toList block.Offset
                @ (block.Windows |> List.collect (snd >> OverSpec >> overExpressions))
            let sources = Option.toList block.From @ (block.Joins |> List.map _.Table)
            Set.union (expressions scope values) (sources |> List.map (source scope) |> Set.unionMany)

        and expressions scope values =
            values
            |> List.collect collectSubqueries
            |> List.map (fun block -> query scope (PlainSelect block))
            |> Set.unionMany

        and source scope = function
            | FromTable table when table.Database.IsNone ->
                match Map.tryFind (nameKey table.Table) scope with
                | Some dependencies -> dependencies.Value
                | None -> Set.empty
            | FromJoinGroup(first, joins) ->
                let reads = source scope first :: (joins |> List.map (_.Table >> source scope))
                Set.union (Set.unionMany reads) (joins |> List.collect Join.conditions |> expressions scope)
            | FromSubquery(body, _) | FromLateral(body, _) -> query scope body
            | FromJsonTable(value, _, _, _) -> expressions scope [ value ]
            | _ -> Set.empty

        let used = selectUses (extend true Map.empty select.Ctes) select
        { select with Ctes = select.Ctes |> List.filter (fun declaration -> Set.contains (nameKey declaration.CteName) used) }

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
    | CastCharset(expression, length, charset) -> CastCharset(mapper expression, length, charset)
    | CastCharAlias(expression, length, alias) -> CastCharAlias(mapper expression, length, alias)
    | CastCharTarget(expression, length, modifiers) -> CastCharTarget(mapper expression, length, modifiers)
    | CastAsBinary(expression, length) -> CastAsBinary(mapper expression, length)
    | BinaryCast expression -> BinaryCast(mapper expression)
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
      Projection: Projection -> Projection
      EnterSelect: SelectStmt -> RewriteRules option
      EnterCtes: CommonTableExpr list -> (RewriteRules * RewriteRules list) option }

let private plainRewriteRules replace projection =
    { Replace = replace
      Projection = projection
      EnterSelect = fun _ -> None
      EnterCtes = fun _ -> None }

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
    rewriteSelectWithCtes rules (rules.EnterCtes select.Ctes) select

and private rewriteSelectWithCtes rules cteScopes (select: SelectStmt) =
    let rules = cteScopes |> Option.map fst |> Option.defaultValue rules
    let rules = rules.EnterSelect select |> Option.defaultValue rules
    let rewriteOrderKey (expression, direction) = rewriteTreeWith rules expression, direction
    let cteRules = cteScopes |> Option.map snd |> Option.defaultWith (fun () -> List.replicate select.Ctes.Length rules)

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
        Ctes = List.map2 (fun scope cte -> { cte with Body = rewriteSelectOrUnion scope cte.Body }) cteRules select.Ctes
        Having = Option.map (rewriteTreeWith rules) select.Having
        OrderBy = List.map rewriteOrderKey select.OrderBy
        Limit = Option.map (rewriteTreeWith rules) select.Limit
        Offset = Option.map (rewriteTreeWith rules) select.Offset }

and private rewriteSelectOrUnion rules =
    function
    | PlainSelect select -> PlainSelect(rewriteSelect rules select)
    | UnionSelect(first, rest, orderBy, limit, offset) ->
        let cteScopes = rules.EnterCtes first.Ctes
        let siblingRules = cteScopes |> Option.map fst |> Option.defaultValue rules
        let rewriteOrderKey (expression, direction) = rewriteTreeWith siblingRules expression, direction

        UnionSelect(
            rewriteSelectWithCtes rules cteScopes first,
            rest |> List.map (fun (kind, select) -> kind, rewriteSelect siblingRules select),
            List.map rewriteOrderKey orderBy,
            Option.map (rewriteTreeWith siblingRules) limit,
            Option.map (rewriteTreeWith siblingRules) offset
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
        let source =
            match delete.Source with
            | SingleDeleteTable _ as table -> table
            | MultiDeleteSource(from, joins) ->
                MultiDeleteSource(rewriteFromItem rules from, List.map (rewriteJoin rules) joins)
        Delete
            { delete with
                Ctes = delete.Ctes |> List.map (fun cte -> { cte with Body = rewriteSelectOrUnion rules cte.Body })
                Where = Option.map rewriteExpression delete.Where
                OrderBy = List.map rewriteOrderKey delete.OrderBy
                Source = source
                Limit = Option.map rewriteExpression delete.Limit }
    | Explain(format, statement) -> Explain(format, rewriteStatementWith rules statement)
    | statement -> statement

/// Rewrites expressions without changing projection aliases.
let rewriteTree replace expression =
    rewriteTreeWith (plainRewriteRules replace id) expression

/// Rewrites every expression inside a SELECT, including nested query bodies.
let rewriteSelectExpressions replace select =
    rewriteSelect (plainRewriteRules replace id) select

let private relationAliases (select: SelectStmt) =
    (Option.toList select.From @ List.map (fun (join: Join) -> join.Table) select.Joins)
    |> List.collect FromItem.leaves
    |> List.choose FromItem.tryQualifier

let internal bindCteColumnScopes outputColumns inherited (definitions: CommonTableExpr list) =
    (inherited, definitions)
    ||> List.mapFold (fun previous cte ->
        let name = cte.CteName.ToLowerInvariant()
        let declared = if cte.CteColumns.IsEmpty then None else Some cte.CteColumns
        let scope = if cte.Recursive then Map.add name declared previous else previous
        let columns = declared |> Option.orElseWith (fun () -> outputColumns scope cte.Body)
        scope, Map.add name columns previous)

let private outerReferenceRules columnBinding replace =
    let rec rulesFor shadowedAliases shadowedColumns cteColumns =
        { Replace =
            (function
            | QualifiedCol(owner, _) as reference when not (Set.contains (owner.ToLowerInvariant()) shadowedAliases) ->
                replace reference
            | Col name as reference
                when shadowedColumns |> Option.exists (fun names -> not (Set.contains (name.ToLowerInvariant()) names)) ->
                replace reference
            | _ -> None)
          Projection = id
          EnterSelect =
            fun nested ->
                let locals =
                    relationAliases nested
                    |> List.map _.ToLowerInvariant()
                    |> Set.ofList
                let columns = shadowedColumns |> Option.bind (fun names ->
                    columnBinding |> Option.bind (fun (getColumns, _) ->
                        getColumns cteColumns nested |> Option.map (Set.union names)))
                Some(rulesFor (Set.union shadowedAliases locals) columns cteColumns)
          EnterCtes =
            fun definitions ->
                match columnBinding, definitions with
                | Some(_, outputColumns), _ :: _ ->
                    // A declaration sees earlier CTEs and enclosing correlations.
                    // The FROM aliases of the SELECT using it are not in scope yet.
                    let declarations, visible =
                        bindCteColumnScopes outputColumns cteColumns definitions
                    Some(rulesFor shadowedAliases shadowedColumns visible, List.map (rulesFor shadowedAliases shadowedColumns) declarations)
                | _ -> None }
    rulesFor Set.empty (columnBinding |> Option.map (fun _ -> Set.empty)) Map.empty

let private outerQualifiedRules replace = outerReferenceRules None replace

let rewriteSubqueryOuterReferences replace expression =
    let rules = outerQualifiedRules replace
    rewriteTreeWith { rules with Replace = fun _ -> None } expression

/// Bare correlated names can be rebound when every intervening scope has known columns.
let internal rewriteSubqueryOuterReferencesWithColumns localColumns outputColumns replace expression =
    let rules = outerReferenceRules (Some(localColumns, outputColumns)) replace
    rewriteTreeWith { rules with Replace = fun _ -> None } expression

let subqueryRelationAliases statement =
    let aliases = System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
    let rules =
        { plainRewriteRules (fun _ -> None) id with
            EnterSelect = fun select ->
                relationAliases select |> List.iter (fun alias -> aliases.Add alias |> ignore)
                None }
    rewriteStatementWith rules statement |> ignore
    List.ofSeq aliases

/// Rewrites qualified outer references in a FROM source while preserving
/// aliases shadowed inside dependent LATERAL and JSON_TABLE subqueries.
let rewriteFromItemOuterReferences replace source =
    rewriteFromItem (outerQualifiedRules replace) source

/// Rewrites qualified references to outer aliases while respecting aliases
/// declared by each nested SELECT, including expression and FROM subqueries.
let rewriteSelectOuterAliases (aliases: Map<string, string>) select =
    let replace = function
        | QualifiedCol(owner, column) ->
            aliases
            |> Map.tryFind (owner.ToLowerInvariant())
            |> Option.map (fun fresh -> QualifiedCol(fresh, column))
        | _ -> None
    rewriteSelect (outerQualifiedRules replace) select

/// Rewrites every executable expression position in a statement.
let rewriteStatement replace statement =
    rewriteStatementWith (plainRewriteRules replace id) statement

/// Names unaliased projections from their original expressions before rewriting.
let rewriteStatementWithProjectionNames projectionName replace statement =
    let retainName (projection: Projection) =
        let alias =
            projection.Alias |> Option.orElseWith (fun () ->
                if projection.SourceName.IsSome then None else projectionName projection.Expression)
        { projection with Alias = alias }
    rewriteStatementWith (plainRewriteRules replace retainName) statement

/// Maps projections throughout a statement, including nested query bodies.
let mapStatementProjections map statement =
    rewriteStatementWith (plainRewriteRules (fun _ -> None) map) statement

let iterStatement visit statement =
    statement
    |> rewriteStatement (fun expression ->
        visit expression
        None)
    |> ignore

/// Literal validation belongs to submission and preparation, not row evaluation.
let literalWarnings statement =
    let warnings = ResizeArray<int * string>()
    statement |> iterStatement (function
        | IntroducedLiteral(Fsdb.Value.VEncodedString(charset, bytes), _, _)
        | ConnectionLiteral(Fsdb.Value.VEncodedString(charset, bytes), _) ->
            let invalidOffset =
                Fsdb.Charset.tryInvalidTextByteOffset charset bytes
                |> Option.orElseWith (fun () -> Fsdb.Charset.tryInvalidBinaryLiteralByteOffset charset bytes)
            match invalidOffset with
            | Some offset ->
                warnings.Add(1300, Fsdb.Charset.invalidCharacterMessage charset bytes offset)
            | None -> ()
        | _ -> ())
    List.ofSeq warnings

let statementCount predicate statement =
    let mutable count = 0

    statement
    |> iterStatement (fun expression ->
        if predicate expression then
            count <- count + 1)

    count

let statementExists predicate statement =
    statementCount predicate statement > 0

let private knownLiteralCharset = function
    | IntroducedLiteral(_, charset, _) -> Some charset
    | CastCharset(_, _, charset) -> Some charset
    | CastCharAlias(_, _, alias) -> Some(CharCastAlias.charset alias)
    | CastCharTarget(_, _, modifiers) -> CharCastModifier.tryCharset modifiers
    | CastAsBinary _ -> Some "binary"
    | ConnectionLiteral(_, collation) -> Some(Fsdb.Collation.charsetOfCollation collation)
    | Lit(Fsdb.Value.VNull | Fsdb.Value.VBytes _ | Fsdb.Value.VBinaryLiteral _ | Fsdb.Value.VBit _)
    | BinaryCast _
    | Cast(_, (TBinary _ | TVarBinary _ | TTinyBlob | TBlob | TMediumBlob | TLongBlob)) -> Some "binary"
    | Collate(_, name) -> Some(Fsdb.Collation.charsetOfCollation name |> Fsdb.Charset.canonicalName)
    | _ -> None

let tryLiteralNodeDiagnostic = function
    | Collate(value, name) ->
        knownLiteralCharset value
        |> Option.bind (fun charset ->
            if Fsdb.Collation.belongsToCharset charset name then None
            else
                Some(1253, sprintf "COLLATION '%s' is not valid for CHARACTER SET '%s'" name charset))
    | FuncCall(name, [ Lit Fsdb.Value.VNull; _ ]) when name.Equals("NAME_CONST", System.StringComparison.OrdinalIgnoreCase) ->
        Some(1382, "The 'NAME_CONST' syntax is reserved for purposes internal to the MySQL server")
    | FuncCall(name, arguments)
        when name.Equals("ST_SIMPLIFY", System.StringComparison.OrdinalIgnoreCase) && arguments.Length <> 2 ->
        Some(1582, "Incorrect parameter count in the call to native function 'ST_Simplify'")
    | FuncCall(name, arguments)
        when name.Equals("ST_MAKEENVELOPE", System.StringComparison.OrdinalIgnoreCase) && arguments.Length <> 2 ->
        Some(1582, "Incorrect parameter count in the call to native function 'ST_MakeEnvelope'")
    | FuncCall(name, arguments) when arguments.Length <> 1
        && (name.Equals("ST_VALIDATE", System.StringComparison.OrdinalIgnoreCase)
            || name.Equals("ST_ISVALID", System.StringComparison.OrdinalIgnoreCase)) ->
        let functionName = if name.Equals("ST_VALIDATE", System.StringComparison.OrdinalIgnoreCase) then "ST_Validate" else "ST_IsValid"
        Some(1582, $"Incorrect parameter count in the call to native function '{functionName}'")
    | FuncCall(name, arguments)
        when name.Equals("ST_FRECHETDISTANCE", System.StringComparison.OrdinalIgnoreCase)
             || name.Equals("ST_HAUSDORFFDISTANCE", System.StringComparison.OrdinalIgnoreCase) ->
        match arguments with
        | [ _; _ ] | [ _; _; _ ] -> None
        | _ ->
            let functionName =
                if name.Equals("ST_FRECHETDISTANCE", System.StringComparison.OrdinalIgnoreCase) then "ST_FrechetDistance"
                else "ST_HausdorffDistance"
            Some(1582, $"Incorrect parameter count in the call to native function '{functionName}'")
    | _ -> None

// Diagnostics depend only on immutable syntax. Weak keys release standalone
// SET expressions and preserve reuse of prepared and stored expressions.
let private literalDiagnostics =
    System.Runtime.CompilerServices.ConditionalWeakTable<Expr, Lazy<(int * string) option>>()

/// Subqueries bind separately, after resolving their own tables and references.
let rec tryLiteralDiagnostic expression =
    // A byref lookup avoids allocating the CLR out-parameter tuple on cache hits.
    let mutable diagnostic = Unchecked.defaultof<_>
    if literalDiagnostics.TryGetValue(expression, &diagnostic) then diagnostic.Value
    else
        literalDiagnostics.GetValue(expression, fun node ->
            lazy (
                children node
                |> List.tryPick tryLiteralDiagnostic
                |> Option.orElseWith (fun () -> tryLiteralNodeDiagnostic node))).Value
