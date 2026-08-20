<!--
sha: c82b752
date: 2026-08-20T15:26:34Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
fsdb server mode: in-memory (no --data-dir, no WAL/fsync)
-->

8 workers, 5.0s measured after 1.0s warmup.

| Workload | Target | ops/sec |
|---|---|---:|
| update-distinct | fsdb | 18670 |
| update-distinct | mysql | 18261 |
| insert | fsdb | 5643 |
| insert | mysql | 26656 |
| replace-distinct | fsdb | 48 |
| replace-distinct | mysql | 24597 |
| mixed | fsdb | 18378 |
| mixed | mysql | 40962 |
