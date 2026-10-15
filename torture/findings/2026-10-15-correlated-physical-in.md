# Indexed correlated IN over physical and pass-through sources

Native MySQL 8.4.11 returned `(1,2)`, `(2,1)`, `(3,1)` for outer rows
`(id,a,b) = (1,1,3), (2,2,NULL), (3,NULL,1)` and indexed inner `k` values
`1`, `2`, `3`, and `NULL`:

```sql
SELECT o.id,
       (SELECT COUNT(*) FROM indexed_inner d
        WHERE d.k IN(o.a+0,o.b+0))
FROM indexed_outer o ORDER BY o.id;
```

The same rows resulted when the inner source was a pass-through derived table
or stable CTE. MySQL also returned two matches for a case-insensitive inner
text key compared with a case-insensitive outer bound, but only one for the
same inner key compared with a binary-collated outer bound.

FSDB already used maintained indexes for correlated equality and range probes,
and statement-local keys for `IN` over materialized sources. It did not use a
physical index for the query above: a leading `TOUCH(d.id)=d.id` residual was
evaluated 4,004 times over 1,000 inner rows and three outer rows before this
change. The physical lookup now resolves stable outer or literal `IN` bounds,
uses exact compatible key types and the effective comparison collation, unions
each membership group's row IDs, and intersects multiple compatible groups
before projecting pass-through rows. It falls back to general evaluation when
no safe, useful index candidate is available, and honors the source table's
`USE`, `FORCE`, and `IGNORE INDEX` controls.

Focused regressions check the direct, derived, and CTE source forms with fewer
than 20 residual calls; a two-index case where each `IN` group alone selects
hundreds of rows but their intersection has two; and the text-collation case
where using the inner index under a binary comparison would omit a result.
An `IGNORE INDEX` regression preserves the same rows and confirms that the
correlated lookup does not use the ignored key.
