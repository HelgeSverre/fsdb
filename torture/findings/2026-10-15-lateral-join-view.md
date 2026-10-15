# Updating a physical target beside a dependent lateral source

Native MySQL 8.4.11 reports `IS_UPDATABLE=YES` for a view whose physical
target precedes a read-only lateral derived source:

```sql
CREATE VIEW lateral_v AS
  SELECT a.id,a.n,d.flag
  FROM a
  JOIN LATERAL (
    SELECT flag FROM b WHERE b.id=a.id LIMIT 1
  ) d ON TRUE;
```

With target rows `(1,0)` and `(2,0)` and only source row `(1,7)`, an update
through the view changes target ID 1 and leaves ID 2 untouched. A
`SQL SECURITY INVOKER` variant returns MySQL error 1356 when the updater can
read and update the target and view but lacks `SELECT` on `b`; granting that
read permission allows the update.

FSDB previously reported the view as non-updatable because its joined-view
classifier excluded `FromLateral`, even though ordinary lateral joins and
their authorization traversal were already supported. The classifier now
keeps the lateral source read-only while exposing the preceding physical
target. A focused regression checks metadata, matched and unmatched writes,
the missing-grant error, and the authorized write. This case does not establish
writable targets that must move ahead of a lateral dependency, nor outer or
multi-component view writes.
