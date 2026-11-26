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

## Backup and restore follow-up

The second pass used TablePlus 26.10.22 build 802, WAL-backed Fsdb
at `2257848b`, and the repository's digest-pinned MySQL 8.4.11 image.
The loopback fixture contained two rows with JSON, binary, Unicode, NULL,
and a microsecond datetime, plus a view and a `BEFORE INSERT` trigger.
Each restore targeted a fresh schema. A separate SQL session checked the
restored objects and rows.

TablePlus **Backup** confirmed the `mysqldump` startup failure. The app
reported error 1229 on the session assignment shown above and left a
zero-byte file. With the same settings, the MySQL backup completed and
produced 4,364 bytes.

### TablePlus restore of a native dump

Both **Restore** runs used the same 4,359-byte file produced by MySQL
8.4.11 `mysqldump`. On MySQL, TablePlus reported completion. The view
returned the original text and the restored trigger trimmed a newly
inserted value.

On Fsdb, TablePlus reported `ERROR 1064 (42000)` at the trigger
definition. Both base rows had already been loaded and were visible
from a second connection. The trigger was absent and `note_view` still
returned the dump's temporary `SELECT 1 AS id, 1 AS body`. The restore
had stopped before the final view definition.

```sql
/*!50003 CREATE*/ /*!50017 DEFINER=`root`@`%`*/
/*!50003 TRIGGER `trim_notes` BEFORE INSERT ON `notes`
FOR EACH ROW SET NEW.body=TRIM(NEW.body) */;
```

The command-line client reproduced 1064 with the same explicit definer
in plain SQL. Removing only the definer comment from the original dump
let the command-line restore finish with the final view and trigger.
TablePlus also completed when the trigger used plain `CREATE TRIGGER`
and the connected account became its definer. For this fixture the
connected account matched the dump's `root@%` definer. Removing an
explicit definer changes ownership when the restore account differs.

## Administration and schema designer follow-up

TablePlus 26.10.22 build 802 connected to WAL-backed Fsdb at `29a281ba`
and the digest-pinned MySQL 8.4.11 image. The two disposable servers
had the same schema, including an enum, a foreign key, and a view.
The test used the TablePlus UI and checked its SQL and committed changes
from separate client sessions.

| Workflow | Observed result |
| --- | --- |
| Switch and create databases | The database picker opened a second schema, and its grid displayed the expected marker row. The **New Database** dialog created a third schema with the same default charset and collation on both servers. |
| Create and rename a table | The Structure designer created `ui_designed` with an auto-increment primary key. Renaming it sent `RENAME TABLE` and refreshed the sidebar on both engines. Independent sessions confirmed the DDL. |
| Show Diagram | Both canvases rendered the tables, the view, and the foreign-key arrow from `audit.ticket_id` to `tickets.id`. |
| Search in Database | A schema-wide **Contains** search for `東京駅` found one row in `tickets` and one in `ticket_names` on both servers. |
| User Management | The account list loaded and **New user** created the test account on both servers. The Global Privileges tab displayed a pre-existing `SELECT` grant on both. The automated attempt to change that checkbox produced no grant on either server, so grant editing remains unverified. |
| Process List | Both displayed an active query in the selected schema and sent `KILL QUERY` for its connection ID. The victim session behaved differently, as detailed below. |

Native `mysqldump` still failed on Fsdb with error 1229 and a zero-byte
output file. The control completed on MySQL. This repeated the command-line
portion of the earlier TablePlus Backup test.

### Killing a running query closes its connection

TablePlus **Tools → Process List → Kill** sent `KILL QUERY <id>` while a
second session ran `SELECT SLEEP(45)`. MySQL returned `1` from the statement
and let the same session run its next query. Fsdb disconnected the second
session, and the client reported error 2013 (`HY000`). The Process List
remained usable on the original connection.

The SQL editor's **Cancel** button opened the same Process List. Killing
the editor's own `SELECT id, SLEEP(40) FROM tickets` showed “Query execution
was interrupted” on MySQL. The next query ran on the same connection ID.
Fsdb showed “Lost connection to MySQL server during query” and the next
query prompted for a reconnect. The query succeeded on a new connection
after using TablePlus's **Reconnect** command.

Repeating the same command with two command-line sessions isolated the
server behavior. Reconnection was disabled, and the controller waited for
the victim statement to appear in `INFORMATION_SCHEMA.PROCESSLIST` before
issuing `KILL QUERY`.

| Victim statement | MySQL 8.4.11 | Fsdb |
| --- | --- | --- |
| `SELECT SLEEP(25)` | Returns `1`. The next command succeeds. | The connection closes. The client reports 2013. |
| `SELECT id, SLEEP(25) FROM tickets` | Returns 1317 (`70100`). The next command succeeds. | The connection closes. The client reports 2013. |
| Same table read after `BEGIN` and an `UPDATE` | Returns 1317. The earlier update can still be read and committed on that session. | The connection closes and the earlier update rolls back. |

The wire server routes explicit query cancellation and peer disconnect
through the same cancellation token. The text-query handler treats the
resulting `OperationCanceledException` as a peer disconnect, so it ends the
command loop. [Wire cancellation handling](../../src/Fsdb/Wire/Server.fs).

The first process-list probe used `SLEEP(90)`. Fsdb intentionally caps
the duration at 60 seconds and returned 1235. The matched cancellation
tests stayed within that limit.
