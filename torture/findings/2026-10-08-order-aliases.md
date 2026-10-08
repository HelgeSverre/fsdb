# ORDER BY alias binding and evaluation

Status: partial. Native MySQL 8.4.11 contracts are executable in
[`order-alias-oracle.py`](../scripts/order-alias-oracle.py).
The oracle uses a disposable native server with 64 MiB buffer and redo limits.

The input table `ordering_values(v INT)` contains `(2),(1)`.

| Query shape | Native result | fsdb at `09c2720d` |
|---|---|---|
| `SELECT -v AS v ... ORDER BY v` | `-2,-1` | agrees |
| `SELECT -v AS v ... ORDER BY v+0` | `-1,-2` | agrees |
| `SELECT v AS a ... ORDER BY ABS(a)` | `1,2` | 1054 |
| Same query with `WHERE FALSE` | succeeds with no rows | 1054 |
| Same query with `GROUP BY v` | `1,2` | 1054 |
| `SELECT SUM(v) AS s ... ORDER BY ABS(s)` | `3` | 1054 |
| `SELECT v AS a ... ORDER BY (SELECT a)` | `1,2` | 1054 in field list |
| `SELECT v AS a,-v AS a ... ORDER BY a` | `(2,-2),(1,-1)` | 1052 |
| Same duplicate projections ordered by `ABS(a+3)` | `(2,-2),(1,-1)` | 1054 |

Bare references prefer projection aliases. References inside expressions prefer
source columns and can fall back to aliases. The duplicate-alias cases above
select the second projection; they do not establish a universal last-alias rule.

## Evaluation count

Starting each case with `SET @n=0`:

```sql
SELECT (@n:=@n+1) AS n FROM ordering_values ORDER BY n;
SELECT @n;
```

Both engines return projection values `1,2` and final `@n=2`.
With `ORDER BY n+0`, MySQL returns projection values `1,3` and final `@n=4`.
At `09c2720d`, fsdb rejects the nested reference with 1054 before changing `@n`.
Consequently, replacing every alias reference with its cached output value
would not match native evaluation semantics.

An unknown name inside `ABS(missing)` returns 1054 / 42S22 in both engines.
The fsdb observations use `QueryHandler.handle` one statement at a time, with
`DOTNET_PROCESSOR_COUNT=8` and a 4 GiB GC heap cap. The maintained script asserts
native results only; it does not claim fsdb parity.

## Implemented binding and materialization

Unique projection aliases and source expression labels bind inside scalar
ordering expressions. Source columns take precedence there; bare aliases retain
their existing output-value precedence. The binding step runs before scalar,
grouped, and window execution dispatch, and preserves projection syntax for
metadata inference. Subquery and window-specification scopes are not rewritten.

Ordinary row assignment aliases reevaluate inside ordering expressions, matching
the `1,3` projection and final `@n=4` above. Grouped aggregate aliases retain their
projected value instead. For example, starting with `@n=0`:

```sql
SELECT SUM(@n:=@n+1) AS s
FROM ordering_values GROUP BY v ORDER BY ABS(s);
SELECT @n;
```

Both engines return `s=1,2` and final `@n=2`. A composite projection
`SUM(v)+(@n:=@n+1) AS s` returns `3,3` and final `@n=2`. Runtime operands retain
the original expression for type and collation inference; their evaluated values
are scoped to the group and matched by node identity.

Aggregate nesting introduced through an alias is rejected with 1111, including
inside `COERCIBILITY(SUM(s))`. A grouped `_latin1'a' AS s` ordered by
`s COLLATE utf8mb4_bin` retains error 1253. Empty input still validates these
expressions. Scalar, grouped, window-result, expression-label, LIMIT, descending,
and prepared-protocol cases have differential coverage.

Validation: `just check` passes 3,037 tests with zero build warnings or errors,
using `DOTNET_PROCESSOR_COUNT=8` and a 4 GiB GC heap cap. The maintained native
oracle passes. Differential contracts pass 50 cases and 5,153 steps with zero
differences at `torture/artifacts/runs/20261008T000306350-85680/contracts`.
The [performance measurement](../../benchmarks/results/6451673a-order-aliases.md)
compares alias and source-expression sorting on the same build.

## Remaining boundaries

- Correlated references such as `ORDER BY (SELECT a)` still fail with 1054.
- Duplicate aliases are not uniformly ambiguous in MySQL. Two different direct
  columns named `a` fail with 1052, identical direct columns succeed, and the
  probed computed projections can take precedence over a direct column. Among
  the tested pairs of computed projections, the first wins. fsdb's selection
  remains incomplete; the native oracle retains these cases.
- Grouped volatile expressions still expose evaluation-order differences. With
  `@n=0`, `SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM ordering_values
  GROUP BY v ORDER BY IF(a=b,v,-v)` returns the same rows `(1,2,2),(5,6,1)` but
  leaves `@n=6` in MySQL and `@n=8` in fsdb. Repeating an assignment-bearing
  aggregate explicitly in the ordering expression also differs from referring
  to its alias: MySQL's projection is `2,4`, while fsdb's is `1,3`; both finish
  at `@n=4`. These observations are fixture-specific evaluation contracts.
- A non-aggregated SELECT ordered by `SUM(a)` has native error 3029; fsdb reports
  1140. A window specification referencing the projection alias has native
  1054 in `window order by`; fsdb uses `order clause` wording.

The native-only oracle includes these open boundaries. They are not enrolled as
accepted differential failures or presented as implemented behavior.

## Duplicate selection order and preparation

For `duplicate_values(v INT,w INT)` containing `(2,10),(1,20)`, each expression
in the following projection lists is named `a`. The observed selection rule
scans matching projections from left to right: direct columns remain candidates,
a distinct direct column makes the reference ambiguous, and the first computed
match stops the scan. A computed projection does not rescue an ambiguity already
encountered earlier in the list.

| Projection expressions, in order | Native selection |
|---|---|
| `v,w,-v` | 1052 / 23000 |
| `v,-v,w` | `-v` |
| `-v,v,w` | `-v` |
| `v,v,w` | 1052 / 23000 |
| `v,v+0,w` | `v+0` |
| `v,w+0,-v` | `w+0` |
| `v,duplicate_values.v` | the same source column; accepted |
| `v,+w,-v` | 1052 / 23000; unary plus does not make a new computed candidate |

Both `ORDER BY a` and `ORDER BY ABS(a+3)` obey these outcomes when the source has
no column named `a`. Distinct table aliases remain distinct sources even when a
join equates their values: `l.v AS a,r.v AS a` is ambiguous under `ON l.v=r.v`.

The tested direct-column/computed ordering also holds through a pass-through
view, mergeable and LIMIT-materialized derived tables, and constant-only derived
tables and views. Source identity therefore matters before literal-view
expansion: replacing source references with literal syntax would misclassify
them as computed candidates.

Star expansion participates in bare-alias selection. `SELECT *,-v AS v ...
ORDER BY v` selects the computed projection, whereas `SELECT *,w AS v ...
ORDER BY v` is ambiguous. Nested references still prefer the source: both forms
accept `ORDER BY ABS(v+3)` and order using the original `v` column.

The native oracle checks these SELECTs and prepares each statement separately.
The ambiguity is a preparation error even with `WHERE FALSE`; it does not depend
on reading rows. At `86b2dbba`, fsdb accepts preparation of the ambiguous triple
`v,w,-v` and raises 1052 only on execution. It also rejects the valid triple
`v,-v,w` and the computed projection after a star with 1052, and reports 1054 for
the identical qualified/unqualified pair inside `ABS(a+3)`.

A shared selector needs to preserve both the selected projection position and
its expression. The position identifies the already-projected value; the
expression supplies type and collation metadata. Name-only lookup cannot choose
a later computed projection correctly. The same source-aware selection must
bind prepared statements and run before view expansion changes expression shape.

The native matrix passes against MySQL 8.4.11 with the disposable server's
64 MiB buffer and redo limits. fsdb preparation/execution observations use the
embedded Debug assembly with `DOTNET_PROCESSOR_COUNT=8` and a 4 GiB GC heap cap.
