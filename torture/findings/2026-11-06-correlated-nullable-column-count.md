# Correlated counts of nullable physical columns

Qualified `COUNT(table.column)` can use the statement-local grouped key plan
even when the physical column is nullable. The grouped query retains the
original `COUNT(column)` expression, so NULL rows contribute zero while
matching non-NULL rows contribute one. This applies to eligible stable scalar
correlations with exact numeric equality keys, including inner joins; other
correlated shapes retain ordinary evaluation.

The pinned MySQL 8.4.11 oracle and fsdb return counts 1, 0, 0, 0 for four
outer categories when one joined timestamp is present and the others are
NULL. A 204-row outer regression keeps the result below 400 examined rows.
With the new dispatch disabled, the same regression examines 2,459 rows and
fails its budget. The proof is therefore about the chosen plan as well as
the returned values.

The non-NULL-column shortcut remains separate: it can count indexed matched
rows without evaluating the column. This nullable path evaluates the column
once per grouped inner row. Coercive outer keys and unstable expressions
continue through the ordinary path.
