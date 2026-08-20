<!--
sha: 6a5d197
date: 2026-08-20T23:27:08Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Workers: 1, 2, 4, 8. 3 trial(s), 5.0s measured after 2.0s warmup.

| Workers | Workload | fsdb ops/sec | fsdb RSD | MySQL ops/sec | MySQL RSD | fsdb/MySQL |
|---:|---|---:|---:|---:|---:|---:|
| 1 | transaction-distinct | 3719 | 1.7% | 4259 | 11.8% | 0.87x |
| 2 | transaction-distinct | 3248 | 56.9% | 7204 | 26.6% | 0.45x |
| 4 | transaction-distinct | 4693 | 24.1% | 9833 | 21.2% | 0.48x |
| 8 | transaction-distinct | 2672 | 18.3% | 11755 | 24.9% | 0.23x |
