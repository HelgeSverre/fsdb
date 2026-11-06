# Dependent UNION CTEs in writable joined views

The [composed-view MySQL oracle](../scripts/composed-join-view-oracle.py)
now includes three CTE chains whose final read-only source is a `UNION ALL`
of two references to an earlier CTE:

| Earlier CTE | UNION body | Ordered flags for target `id=1` |
| --- | --- | --- |
| Windowed `ROW_NUMBER()` | Both branches read the windowed result | 1, 2, 11, 12 |
| Windowed `ROW_NUMBER()` | Both branches read it; global `ORDER BY flag DESC LIMIT 2` | 11, 12 |
| Recursive number sequence | Both branches read the recursive result | 7, 8, 8, 17, 18, 18 |

Each chain joins a physical writable table after the UNION source. MySQL
8.4.11 reports `IS_UPDATABLE=YES`, changes the target once despite duplicate
read rows, rejects INSERT and DELETE with 1471 and 1395, and rejects an
invalid cascaded CHECK OPTION update with 1369 without changing the target.
The native oracle passes **43 composed view shapes** after these additions.

The fsdb view regression checks the same three chains, output flags,
metadata, one physical update, atomic CHECK OPTION rejection, and INSERT and
DELETE refusal. No engine change was needed. The source remains read-only;
these checks establish writability only for the joined physical component.
Other CTE compositions, layered security contexts, and concurrent write
arrangements remain open.

Verification:

```sh
uv run --with pymysql torture/scripts/composed-join-view-oracle.py
dotnet run --project tests/Fsdb.Tests -- --sequenced --no-spinner \
  --filter-test-case 'window and recursive UNION CTE chains keep a joined target writable'
```
