# Grouped derived-star sources in updatable join views

On native MySQL 8.4.11, a join view stays updatable when its read-only
derived source projects a parenthesized join with `USING` or `NATURAL JOIN`.
The source may use `*` or a qualified star. A physical component of the outer
join remains a valid UPDATE target:

```sql
CREATE TABLE a(id INT PRIMARY KEY,n INT);
CREATE TABLE b(id INT PRIMARY KEY,flag INT);
CREATE TABLE c(id INT PRIMARY KEY,aux INT);
INSERT INTO a VALUES(1,0);
INSERT INTO b VALUES(1,7);
INSERT INTO c VALUES(1,9);

CREATE VIEW v AS
  SELECT a.id AS aid,a.n,d.flag
  FROM a JOIN (SELECT * FROM (b JOIN c USING(id))) d ON d.id=a.id;
UPDATE v SET n=n+1 WHERE aid=1;
SELECT id,n FROM a; -- (1,1)
```

MySQL reports `IS_UPDATABLE=YES` for this view. The same outcome holds for
`SELECT b.* FROM (b JOIN c USING(id))` and `SELECT * FROM (b NATURAL JOIN c)`;
three consecutive updates leave `a.n=3`. FSDB previously reported the view
as updatable in metadata but rejected the UPDATE with error 1288. Its
write-target classifier inferred names for a derived star by reading only
top-level table items. A grouped join had no top-level qualifier, so the
classifier could not resolve the derived output.

The classifier now resolves leaf column names, then uses the shared logical
join-column routine for unqualified stars. This preserves `USING`/natural
merged-column order and still resolves qualified stars against their leaf.
The focused Expecto case covers all three forms and verifies the base row.
Broader join-source reorderings and other complex view shapes remain open.
