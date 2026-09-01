<!--
sha: 98bc883
date: 2026-09-01T08:29:45Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Workers: 8. 1 trial(s), 5.0s measured after 1.0s warmup.

| Workers | Workload | fsdb ops/sec | fsdb RSD | fsdb retry | MySQL ops/sec | MySQL RSD | MySQL retry | fsdb/MySQL |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 8 | point-read | 12677 | 0.0% | 0.0% | 105522 | 0.0% | 0.0% | 0.12x |
| 8 | update-distinct | 4721 | 0.0% | 0.0% | 11011 | 0.0% | 0.0% | 0.43x |
| 8 | update-hot | 6026 | 0.0% | 0.0% | 15438 | 0.0% | 0.0% | 0.39x |
| 8 | upsert-distinct | 4225 | 0.0% | 0.0% | 8085 | 0.0% | 0.0% | 0.52x |
| 8 | insert | 5569 | 0.0% | 0.0% | 15319 | 0.0% | 0.0% | 0.36x |
| 8 | replace-distinct | 5662 | 0.0% | 0.0% | 14255 | 0.0% | 0.0% | 0.40x |
| 8 | transaction-distinct | 2038 | 0.0% | 0.0% | 9225 | 0.0% | 0.0% | 0.22x |
| 8 | mixed | 6974 | 0.0% | 0.0% | 25746 | 0.0% | 0.0% | 0.27x |
