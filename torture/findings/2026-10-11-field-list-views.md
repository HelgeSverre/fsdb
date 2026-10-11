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

After dropping the table beneath a still-defined view, MySQL returned error
1356 with the view's qualified name and the standard invalid-view message.
Fsdb previously returned 1146 because the column descriptor could no longer
resolve the source table. The wire handler now distinguishes an invalid stored
view from an absent relation and returns 1356; a focused raw-wire regression
asserts the code and message.

A metadata probe found another view-specific rule. A base table's `CHAR`,
`VARCHAR`, `TEXT`, and `VARBINARY` columns reported descriptor scale 0, while
the same columns through a view reported 31. `ENUM`, `SET`, `JSON`, `BLOB`, and
`GEOMETRY` view columns also reported 31; numeric and temporal types retained
their declared scales. A raw-wire regression caught fsdb reporting 0 for a
view's `VARCHAR(20)` column. The view wire mapper now applies 31 to these
string-like type IDs without changing physical-table metadata.
