<!--
sha: 97b52d6
date: 2026-08-25T23:34:31Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Workers: 8. 1 trial(s), 5.0s measured after 1.0s warmup.

| Workers | Workload | fsdb ops/sec | fsdb RSD | fsdb retry | MySQL ops/sec | MySQL RSD | MySQL retry | fsdb/MySQL |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 8 | point-read | 11741 | 0.0% | 0.0% | 85799 | 0.0% | 0.0% | 0.14x |
| 8 | update-distinct | 5279 | 0.0% | 0.0% | 13553 | 0.0% | 0.0% | 0.39x |
| 8 | update-hot | 8744 | 0.0% | 0.0% | 19610 | 0.0% | 0.0% | 0.45x |
| 8 | upsert-distinct | 7099 | 0.0% | 0.0% | 15811 | 0.0% | 0.0% | 0.45x |
| 8 | insert | 11402 | 0.0% | 0.0% | 17419 | 0.0% | 0.0% | 0.65x |
| 8 | replace-distinct | 4440 | 0.0% | 0.0% | 12156 | 0.0% | 0.0% | 0.37x |
| 8 | transaction-distinct | 2530 | 0.0% | 0.0% | 2637 | 0.0% | 0.0% | 0.96x |
| 8 | mixed | 3867 | 0.0% | 0.0% | 3917 | 0.0% | 0.0% | 0.99x |
