# Pending YEAR inserts in limited ordered writes

MySQL 8.4.11 waits for an uncommitted `YEAR` index insertion that can become
the first row of a limited ordered write. On a keyless table with
`KEY ix_year(rank_year)` and visible years 2021, 2022, and 2023, a pending
2020 insert blocks `UPDATE` or `DELETE ... ORDER BY rank_year ASC LIMIT 1`.
A pending 2024 insert likewise blocks the descending form. After the owner
commits, MySQL updates or deletes the inserted row and reports one affected
row in all four cases. The [disposable oracle](../scripts/keyless-year-ordered-write-oracle.py)
checks the wait, affected count, and final rows.

Fsdb previously completed the ascending UPDATE before the owner committed
and selected the visible 2021 row. Its ordered insert-claim path omitted
`YEAR`, even though the index encoder already represents stored year values
with its integer `I` key. The scheduler now includes `YEAR` in ordered insert
groups and decodes that key as a number. The four-case transaction regression
failed before the change and passes afterward. This covers a single `YEAR`
secondary index on a keyless table with `LIMIT 1`; other predicates, composite
keys, and wider isolation histories remain open.

```sh
uv run --with pymysql python torture/scripts/keyless-year-ordered-write-oracle.py
```
