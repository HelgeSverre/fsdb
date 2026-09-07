<!--
sha: 3527901
date: 2026-09-07T23:18:51Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

ShortRun comparison after extending conservative direct autocommit execution
from ordinary inserts to physical-table `REPLACE` and single-table writes.

| Method | Target | Mean | StdDev | Allocated |
|---|---|---:|---:|---:|
| ReplaceNewRow | fsdb | 180.7 us | 1.83 us | 1,280 B |
| ReplaceNewRow | mysql | 120.7 us | 3.96 us | 1,280 B |
| ReplaceExistingByPk | fsdb | 177.4 us | 3.90 us | 1,111 B |
| ReplaceExistingByPk | mysql | 140.4 us | 1.30 us | 1,111 B |
| UpdateSingleRow | fsdb | 266.1 us | 1.92 us | 680 B |
| UpdateSingleRow | mysql | 121.6 us | 1.52 us | 743 B |
| UpsertExistingByPk | fsdb | 251.3 us | 6.09 us | 1,511 B |
| UpsertExistingByPk | mysql | 123.3 us | 0.99 us | 1,510 B |

Against the earlier [quick run](98bc883-quick.md), fsdb's new-row
`REPLACE` fell from 294.65 us to 180.7 us, existing-row `REPLACE` from
241.26 us to 177.4 us, and point `UPDATE` from 291.52 us to 266.1 us.
Those changes are reductions of 39%, 26%, and 9%, respectively.

`UpsertExistingByPk` is included as a current reference. Its `INSERT` AST
shape already used the ordinary-insert fast path from the preceding change,
so it is not an additional gain from this extension.

The direct path is deliberately limited to physical single-table statements
that cannot invoke registered or stored scalar functions and contain no
expression subqueries. Views, CTE or join-shaped writes, multi-table deletes,
and statements that can initiate nested database writes retain a private
transaction root for statement atomicity.
