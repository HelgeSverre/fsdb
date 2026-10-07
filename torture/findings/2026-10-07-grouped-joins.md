# Grouped right join operands

Status: open. Baseline `18eb1f6a` rejects a grouped right operand with
1064 / 42000. Left-associated parenthesized chains remain supported.

The [native oracle](../scripts/grouped-join-oracle.py) passes against disposable
MySQL 8.4.11:

```sh
python3 torture/scripts/grouped-join-oracle.py
```

The fixture has `a.id = {1,2}`, `b.id = {1,3}`, and `c.id = {3}`. Every query
selects `a.id,b.id,c.id` ordered by those columns.

| FROM clause | Native rows |
|---|---|
| `a LEFT JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id` | `(1,NULL,NULL)`, `(2,NULL,NULL)` |
| `(a LEFT JOIN b ON a.id=b.id) JOIN c ON b.id=c.id` | empty |
| `a JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id` | empty |
| `a LEFT JOIN (b,c) ON a.id=b.id` | `(1,1,3)`, `(2,NULL,NULL)` |
| `a LEFT JOIN (b LEFT JOIN c ON b.id=c.id) ON a.id=b.id` | `(1,1,NULL)`, `(2,NULL,NULL)` |

The first two cases prove that removing parentheses and appending joins to the
flat chain changes outer-join results. The inner ON expression in
`a LEFT JOIN (b JOIN c ON c.id=a.id) ON a.id=b.id` cannot reference `a`;
MySQL returns 1054 / 42S22. The enclosing ON may reference both grouped sources.

Fsdb's `FromItem` represents one physical, derived, lateral, or JSON_TABLE
source, while SELECT retains a base source followed by a flat `Join list`.
`fromJoinChain` only flattens parentheses on the left. Supporting the right
operand requires a grouped relation that preserves its column qualifiers,
inner name scope, and NULL-extension boundary. A synthetic aliased SELECT
would hide the original qualifiers and is not an equivalent representation.
Mutation targets, view expansion, authorization, metadata, and persistence
also consume FROM sources and must retain the group boundary when that shape
is introduced. The current refusal remains preferable to flattening it into
a query with different results.
