<!--
sha: 48ea75e
date: 2026-08-20T22:34:16Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Workers: 1, 2, 4, 8, 16. 3 trial(s), 10.0s measured after 2.0s warmup.

| Workers | Workload | fsdb ops/sec | fsdb RSD | MySQL ops/sec | MySQL RSD | fsdb/MySQL |
|---:|---|---:|---:|---:|---:|---:|
| 1 | point-read | 8906 | 0.6% | 25447 | 0.4% | 0.35x |
| 1 | update-distinct | 10974 | 1.7% | 8428 | 0.6% | 1.30x |
| 1 | update-hot | 10267 | 26.7% | 7510 | 16.7% | 1.37x |
| 1 | upsert-distinct | 11620 | 0.4% | 8353 | 0.2% | 1.39x |
| 1 | insert | 14482 | 3.5% | 8975 | 3.3% | 1.61x |
| 1 | replace-distinct | 14034 | 2.0% | 7681 | 3.7% | 1.83x |
| 1 | transaction-distinct | 210 | 1.3% | 4969 | 1.6% | 0.04x |
| 1 | mixed | 10123 | 5.7% | 12670 | 5.8% | 0.80x |
| 2 | point-read | 13590 | 1.8% | 45638 | 1.3% | 0.30x |
| 2 | update-distinct | 16754 | 1.3% | 14917 | 5.4% | 1.12x |
| 2 | update-hot | 20237 | 1.9% | 15296 | 1.6% | 1.32x |
| 2 | upsert-distinct | 17810 | 3.4% | 14224 | 5.0% | 1.25x |
| 2 | insert | 21748 | 5.8% | 14035 | 2.5% | 1.55x |
| 2 | replace-distinct | 21143 | 2.9% | 12057 | 1.8% | 1.75x |
| 2 | transaction-distinct | 16 | 2.8% | 8368 | 6.4% | 0.00x |
| 2 | mixed | 16012 | 0.4% | 23805 | 1.6% | 0.67x |
| 4 | point-read | 17360 | 2.9% | 73189 | 1.1% | 0.24x |
| 4 | update-distinct | 20855 | 0.2% | 21216 | 0.8% | 0.98x |
| 4 | update-hot | 20114 | 36.8% | 15793 | 18.7% | 1.27x |
| 4 | upsert-distinct | 19954 | 8.8% | 18289 | 10.3% | 1.09x |
| 4 | insert | 28145 | 5.2% | 17964 | 12.6% | 1.57x |
| 4 | replace-distinct | 25486 | 6.4% | 15712 | 23.6% | 1.62x |
| 4 | transaction-distinct | 16 | 2.4% | 11411 | 3.6% | 0.00x |
| 4 | mixed | 19144 | 0.4% | 27877 | 3.4% | 0.69x |
| 8 | point-read | 16208 | 3.7% | 97978 | 6.0% | 0.17x |
| 8 | update-distinct | 22657 | 0.8% | 25839 | 13.4% | 0.88x |
| 8 | update-hot | 25701 | 3.3% | 23363 | 4.9% | 1.10x |
| 8 | upsert-distinct | 23468 | 0.8% | 22432 | 3.3% | 1.05x |
| 8 | insert | 31903 | 1.2% | 25740 | 8.3% | 1.24x |
| 8 | replace-distinct | 28160 | 2.3% | 18275 | 15.9% | 1.54x |
| 8 | transaction-distinct | 16 | 1.1% | 11682 | 13.8% | 0.00x |
| 8 | mixed | 19929 | 1.5% | 34053 | 9.7% | 0.59x |
| 16 | point-read | 17266 | 3.5% | 104995 | 3.1% | 0.16x |
| 16 | update-distinct | 22935 | 1.2% | 28187 | 16.5% | 0.81x |
| 16 | update-hot | 27314 | 1.4% | 24992 | 12.5% | 1.09x |
| 16 | upsert-distinct | 23802 | 0.2% | 30365 | 4.9% | 0.78x |
| 16 | insert | 32990 | 2.9% | 29724 | 11.3% | 1.11x |
| 16 | replace-distinct | 28996 | 0.7% | 30805 | 8.9% | 0.94x |
| 16 | transaction-distinct | 15 | 1.1% | 17877 | 11.4% | 0.00x |
| 16 | mixed | 20566 | 0.1% | 56340 | 5.5% | 0.37x |
