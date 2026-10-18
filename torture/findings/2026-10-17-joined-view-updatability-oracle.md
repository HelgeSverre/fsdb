# Joined-view updatability metadata oracle

Finding 149 claims `INFORMATION_SCHEMA.VIEWS.IS_UPDATABLE` must be `NO` when a
joined view exposes only columns from a read-only aggregate source, even if
another joined source is writable. MySQL 8.4.11 reports `YES` for that shape,
while rejecting an actual update with error 1288. Fsdb matches both results.

The disposable MySQL oracle and fsdb server ran:

```sql
CREATE TABLE physical_table (id INT PRIMARY KEY);
INSERT INTO physical_table VALUES (1);
CREATE VIEW aggregate_view AS
  SELECT COUNT(*) AS total FROM physical_table;
CREATE VIEW joined_only_aggregate AS
  SELECT a.total FROM physical_table p
  JOIN aggregate_view a ON 1=1;

SELECT TABLE_NAME, IS_UPDATABLE
FROM information_schema.VIEWS
WHERE TABLE_SCHEMA = 'fsdb_viewmeta';
UPDATE joined_only_aggregate SET total = 2;
```

Both engines returned `NO` for `aggregate_view`, `YES` for
`joined_only_aggregate`, and error 1288 for the attempted update. MySQL's
metadata classification is broader than the existence of a writable projected
column in this case. The reported fsdb behavior is intentional compatibility;
changing it to `NO` would create a metadata divergence.
