# ORDER BY alias binding and evaluation

Status: open. Native MySQL 8.4.11 contracts are executable in
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
fsdb rejects the nested reference with 1054 before changing `@n`.
Consequently, replacing every alias reference with its cached output value
would not match native evaluation semantics.

An unknown name inside `ABS(missing)` returns 1054 / 42S22 in both engines.
The fsdb observations use `QueryHandler.handle` one statement at a time, with
`DOTNET_PROCESSOR_COUNT=8` and a 4 GiB GC heap cap. The maintained script asserts
native results only; it does not claim fsdb parity.

Binding, evaluation, and metadata must share the same alias scope. Empty-source,
grouped, and nested-query cases require coverage beyond changing the runtime
sort-key resolver. Duplicate-name selection needs further native controls before
changing the existing ambiguity policy.
