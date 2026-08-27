<!--
sha: f0d74a8
date: 2026-08-27T21:29:05Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Workers: 4, 8, 16. 1 trial(s), 5.0s measured after 1.0s warmup.

| Workers | Workload | fsdb ops/sec | fsdb RSD | fsdb retry | MySQL ops/sec | MySQL RSD | MySQL retry | fsdb/MySQL |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 4 | update-distinct | 7100 | 0.0% | 0.0% | 12521 | 0.0% | 0.0% | 0.57x |
| 8 | update-distinct | 6732 | 0.0% | 0.0% | 14542 | 0.0% | 0.0% | 0.46x |
| 16 | update-distinct | 5683 | 0.0% | 0.0% | 18520 | 0.0% | 0.0% | 0.31x |
