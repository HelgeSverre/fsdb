<!--
sha: 4f83a0d
date: 2026-08-20T19:55:49Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
fsdb server mode: in-memory (no --data-dir, no WAL/fsync)
-->

8 workers, 5.0s measured after 1.0s warmup.

| Workload | Target | ops/sec |
|---|---|---:|
| update-distinct | fsdb | 21962 |
| update-distinct | mysql | 19620 |
| insert | fsdb | 30751 |
| insert | mysql | 23984 |
| replace-distinct | fsdb | 1157 |
| replace-distinct | mysql | 23440 |
| mixed | fsdb | 18037 |
| mixed | mysql | 39427 |
