# TablePlus compatibility on macOS

TablePlus 26.10.22 build 802 connected to WAL-backed Fsdb with engine
code at `789a3519` and to native MySQL 8.4.11 on the same Mac. Both
servers used disposable loopback listeners and the same `tp_compat`
fixture. The fixture included tables with single, composite, and absent
primary keys, a foreign key, an enum, JSON, binary data, Unicode, a
checked view, a procedure, a function, and a trigger.

The test drove TablePlus through the macOS accessibility tree, captured
its statements with `mysql.general_log`, and checked writes from a
second session. It compared rendered results and actual CSV files.
[TablePlus connection and URL documentation](https://docs.tableplus.com/gui-tools/manage-connections).

## Verified workflows

| Workflow | Observed result |
| --- | --- |
| Connect and browse | Both connections opened the intended schema. The sidebar listed tables, the checked view, the procedure, and the function. |
| Data grid | Both showed decimal values, dates, microsecond datetimes, NULLs, JSON, binary data, Unicode, and embedded line breaks from the fixture. |
| Structure, indexes, and DDL | The columns and composite secondary index displayed. The foreign-key reference appeared in the table DDL. The trigger, procedure definition, and function definition opened in their respective views. |
| View data | The checked view displayed Ada and 東京駅 on both engines. |
| Paging, filtering, and sorting | Both showed rows 1–300 and then 301–325. The raw filter `id BETWEEN 319 AND 322` returned four rows. Clicking the ID column reversed their order. |
| Grid inserts and edits | The inserted customer received ID 4, the default enum value, and a NULL email. A subsequent edit persisted. The composite-key edit changed the intended tag, and the keyless edit updated only the intended row with a duplicate label. |
| Grid deletion | Deleting the inserted customer and committing from the same grid left the original three rows on both engines. |
| SQL editor | Queries displayed routine results, JSON, NULL, `EXPLAIN FORMAT=JSON`, and `SHOW FULL PROCESSLIST`. A missing table produced the expected error. A table created in the editor was visible from a separate session. |
| SQL transaction and trigger | The editor saw its inserted row and the uppercase trigger result inside the transaction. A second session saw zero matching rows before and after rollback. |
| CSV export | The two files parsed to equivalent rows, including binary hex values, microsecond timestamps, quoted text, and Unicode. JSON spacing differed in the original CSV bytes. |
| CSV import | The import wizard created a table from a two-row CSV on each engine. Both tables contained the supplied IDs, the Japanese text with an embedded comma, and the second text value. |
| TLS and privileges | A separate Fsdb instance required TLS. TablePlus authenticated a `caching_sha2_password` account, showed TLSv1.2, and browsed with `SELECT` privileges. An update was denied. |

## Confirmed differences

### Filtering binary log text

The TablePlus SQL editor returned a count on MySQL for this query. On
Fsdb the same query raised 3995, which the editor displayed.

```sql
SELECT COUNT(*) FROM mysql.general_log WHERE argument REGEXP '^SELECT';
```

Both servers report `argument` as `MEDIUMBLOB`. A CLI control showed
the same divergence on a temporary `MEDIUMBLOB` column containing
`SELECT 1` and `QUIT`. MySQL returned 1 and 0. Fsdb raised 3995.
Both servers raised 3995 for `_binary'abc' REGEXP 'a'`. The control
narrows the gap to the tested column expression and preserves the
binary literal as a regression control.

### Index size in the table inspector

For the 325-row `pages` table with a secondary index, TablePlus showed
16 KB of data and a blank index size on Fsdb. MySQL showed 16 KB for
each. The client's `INFORMATION_SCHEMA.TABLES` query returned
`DATA_LENGTH=16384, INDEX_LENGTH=0` on Fsdb and `16384, 16384` on
MySQL. This is the visible consequence of Fsdb's current size estimate.
[Table-size metadata findings](2026-10-11-table-size-metadata.md).

### Native mysqldump startup

The MySQL 8.4.11 `mysqldump` executable completed against MySQL and
exited with status 2 and zero dump bytes against Fsdb. Its first
failure was error 1229 on `NET_WRITE_TIMEOUT`.

```sql
SET SESSION NET_READ_TIMEOUT=86400, SESSION NET_WRITE_TIMEOUT=86400;
```

MySQL accepts this session assignment. Fsdb handles `net_write_timeout`
as a global-only variable on this path. TablePlus's tested CSV export
queries the server directly and produced a complete file.

```sh
mysqldump --no-defaults --protocol=TCP -h 127.0.0.1 -P "$port" \
  -u root --ssl-mode=DISABLED --no-tablespaces --set-gtid-purged=OFF \
  --column-statistics=0 --routines --triggers tp_compat
```

Both editors rendered the sampled JSON plan with `range` access through
`PRIMARY`. Fsdb omitted the MySQL cost fields. The planner gap is
tracked under EXPLAIN fidelity.
[Planner gaps](../../GAPS.md#2-query-execution).
