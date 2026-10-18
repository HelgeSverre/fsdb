# Correlated byte equality over materialized rows

MySQL 8.4.11 distinguishes `X'41'` from `X'4100'` in a `VARBINARY` column,
returns no equality match for a NULL outer bound, and compares a byte column
with a character column using the compatible byte sequence. Fixed-width
`BINARY(3)` storage pads both inserted `X'41'` and `X'4100'` to `410000`.

Fsdb already returned the matching rows, but a correlated equality against a
stable materialized byte column scanned that source for each outer row. The
statement-local materialized equality lookup now keys `BINARY`, `VARBINARY`,
and `BLOB`-family columns by their stored bytes. Binary literals use the same
key. The original SQL predicate remains the final result check. A probe with
an incompatible value type, including a character string, falls back to the
scan so its comparison coercion remains authoritative.

The focused regression uses 1,000 distinct inner byte keys and four outer
rows. Exact byte probes return counts `(1,1,0,0)` and evaluate the `TOUCH`
residual fewer than 30 times, including the engine's metadata work. A mixed
byte/string probe returns MySQL's matching row while evaluating the residual
over 900 times, confirming the safe fallback. The MySQL oracle also checked
the distinct `X'41'`/`X'4100'`, NULL, mixed character, and fixed-width padding
cases. Broader cross-type coercions are not claimed indexed by this rule.
