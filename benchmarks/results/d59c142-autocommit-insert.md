<!--
sha: d59c142
date: 2026-09-07T23:02:04Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

ShortRun comparison for the ordinary autocommit `INSERT` fast path. The
serial and concurrent bursts each execute sixteen distinct single-row
inserts; they differ only in whether those round trips overlap.

| Method | Target | Mean | StdDev | Allocated |
|---|---|---:|---:|---:|
| ConcurrentInsertBurst | fsdb | 2,382.1 us | 36.85 us | 48.9 KB |
| SequentialInsertBurst | fsdb | 3,026.1 us | 35.19 us | 23.38 KB |
| ConcurrentInsertBurst | mysql | 564.9 us | 29.06 us | 49.27 KB |
| SequentialInsertBurst | mysql | 1,832.4 us | 23.18 us | 23.38 KB |
| InsertSingle | fsdb | 190.4 us | 2.04 us | 1.34 KB |
| InsertSingle | mysql | 116.5 us | 1.95 us | 1.34 KB |

Against the immediately preceding local run on `7186dda`, fsdb's single
insert fell from 245.7 us, the sequential burst from 4,151 us, and the
concurrent burst from 3,655 us. The historical fixed-query bisect measured
the original private-root change at 162.6 us before and 232.4 us after; the
new conservative direct path measured 173.2 us on the same workload.

MySQL's concurrent ShortRun samples varied materially between the two runs,
so the fsdb before/after numbers are the useful comparison. The remaining
fsdb/MySQL difference is still substantial and is tracked as statement and
publication overhead rather than row-store scaling.
