# Straight joins and rendered FROM chains

Status: implemented.

The table form `a STRAIGHT_JOIN b` carries an explicit `StraightJoin` AST kind.
It uses inner-join matching while keeping the written left source before the
right source. Ordinary and full-text join reordering exclude chains containing
this kind; compatible index probes and candidate bounds remain available.
Conservatively retaining the complete written chain leaves partial reordering
around individual constraints within the general optimizer gap.

MySQL 8.4.11 accepts ON, USING, and an omitted condition. Fsdb supports these
forms for reads, joined UPDATE/DELETE, updatable views, LATERAL sources, and
JSON_TABLE sources. View metadata retains the inner join's updatability rules.

SQL rendering preserves the straight-join kind. The parser accepts parentheses
around the left FROM chain, including repeated parentheses and subsequent
joins, without changing left association. This covers the grouped FROM text
emitted by the view renderer. Grouped right operands still require a richer
join tree and are outside this implementation.

## Evidence

The [native oracle](../scripts/fulltext-join-bounds-oracle.py) verifies optional
conditions, parenthesized joins, mutation row counts, view updatability and
updates, and LATERAL/JSON_TABLE results on a disposable MySQL 8.4.11 server.

```sh
python3 torture/scripts/fulltext-join-bounds-oracle.py
```

Expecto checks matching execution results, preservation of the AST kind through
view rendering and parsing, left association of parenthesized chains, and
written order in ordinary and full-text plans. The parser regression fails
with a syntax error before table-level STRAIGHT_JOIN support.

The complete gate passes 2,939 tests without build warnings or errors. The
contract lane passes 49 cases / 5,117 steps without differences at
`20261007T103918427-74525/contracts`. A separate test-only commit gives the
transaction publication race dedicated workers after the gate exposed a
shared-thread-pool readiness timeout; the readiness and publication assertions
remain intact.

The [mixed-type IN mismatch](2026-10-07-fulltext-join-bounds.md#mixed-type-in-remains-open)
is a separate, unresolved difference in MySQL's plan-dependent predicate
substitution. Accepting STRAIGHT_JOIN makes both native join orders directly
reproducible in fsdb; it does not emulate that substitution.
