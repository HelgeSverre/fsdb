# Field-list metadata for views

The digest-pinned MySQL 8.4.11 server answered raw `COM_FIELD_LIST` requests
for both a physical table and a view over it. For a view defined as
`CREATE VIEW v AS SELECT id AS view_id, note FROM t`, the returned column
definitions used the view's schema and name for both table fields and used
`view_id` and `note` for each column's display and original names. A `%`
wildcard returned both columns.

Fsdb previously resolved `COM_FIELD_LIST` only through a physical table
snapshot, returning 1146 for the view. It now uses the existing session-aware
view column descriptor when no physical table exists, then applies the same
wildcard and wire metadata path as a table. A raw-wire regression covers the
view alias, schema/table names, and EOF termination.
