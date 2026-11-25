# Native MySQL-protocol OLTP peers

Measured on 2026-11-25 on the Apple M2 Max Mac (12 cores, 32 GiB,
macOS 15.6) using Fsdb source `8f55e71c`, Release .NET 10.0.12,
and MySqlConnector 2.6.2. Only one native server runs at a time.

| Target | Version and settings |
| --- | --- |
| Fsdb | CLI 0.1.9 line, WAL enabled, `DOTNET_gcServer=1`. |
| MySQL | 8.4.11, InnoDB, 256 MiB buffer pool, commit flush set to 1, binary log off. |
| MariaDB | 11.8.10, InnoDB, 256 MiB buffer pool, commit flush set to 1, binary log off. |
| Dolt | 2.7.7, default journal configuration and ordinary SQL commits. `dolt_transaction_commit=OFF` was verified. [Dolt journal](https://www.dolthub.com/blog/2025-12-10-journal-data-loss-detection/) |

This measures local durable configurations. A power-loss test is outside
the measurement, and the engines have different storage designs.
MySQL 8.4 remains the compatibility reference. TiDB is a separate
distributed comparison because its local deployment adds storage and
coordination processes.

## Method

Each trial reseeds 10,000 rows into the same schema:

```sql
CREATE TABLE users (
    id INT PRIMARY KEY,
    age INT NOT NULL,
    name VARCHAR(40) NOT NULL,
    KEY ix_age(age)
);
```

Point reads select `id, name, age` for a random primary key. Mixed traffic
does about half of those reads and half autocommit point updates, using
disjoint update ranges per worker. A transaction counts as one operation:
`START TRANSACTION`, two updates to the worker's range, and `COMMIT`.
Each update increments the indexed age column. Clients request Read
Committed per connection, use loopback TCP with pooling and TLS disabled,
and keep one operation outstanding. The client validates returned IDs and
names and checks the committed change in `SUM(age)` after every workload.

Each measurement has 1.5 seconds of warmup and 6 seconds timed. Two
trials reverse engine order, worker counts, and workload order. The
tables average the two trial statistics. RSS records the highest sampled
resident memory of the server process per trial. Higher throughput and
lower latency are better. These are short samples on a shared host.
The machine already had about 13 GiB of swap in use. Swap-ins continued
during the run and sampled swap-outs were flat. Absolute throughput,
especially Fsdb and MySQL write throughput, varied between passes.

[Raw results](2026-11-25-native-oltp-peers.csv)

## Throughput (operations per second)

| Workers | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | point | 4,441 | 26,155 | 29,456 | 16,230 |
| 1 | mixed | 3,422 | 13,895 | 22,358 | 264 |
| 1 | transaction | 1,019 | 4,553 | 7,638 | 153 |
| 8 | point | 14,600 | 93,332 | 88,491 | 57,996 |
| 8 | mixed | 11,731 | 48,305 | 48,504 | 359 |
| 8 | transaction | 2,705 | 15,488 | 17,807 | 184 |
| 16 | point | 21,055 | 107,728 | 103,248 | 66,936 |
| 16 | mixed | 11,651 | 65,613 | 78,756 | 377 |
| 16 | transaction | 2,877 | 20,146 | 22,814 | 189 |

At 16 workers Fsdb reached about 20%, 18%, and 14% of MySQL throughput
for point reads, mixed traffic, and two-update transactions respectively.
That mixed ratio was 21% in the first pass and 13% in the second. MariaDB
was in the same throughput range as MySQL and led the mixed and
transaction cases on average. Dolt read faster than Fsdb at every worker
count. For 16-worker mixed traffic and transactions, Fsdb completed about
31× and 15× as many operations respectively. These relative positions
apply to this indexed schema and the tested settings.

## p99 round-trip latency (ms)

| Workers | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | point | 0.85 | 0.10 | 0.08 | 0.17 |
| 1 | mixed | 0.81 | 0.32 | 0.12 | 20.28 |
| 1 | transaction | 3.89 | 0.86 | 0.30 | 13.07 |
| 8 | point | 14.45 | 0.33 | 0.23 | 0.45 |
| 8 | mixed | 15.22 | 0.42 | 1.75 | 55.35 |
| 8 | transaction | 21.64 | 1.33 | 1.77 | 59.63 |
| 16 | point | 16.08 | 0.64 | 0.44 | 1.10 |
| 16 | mixed | 20.02 | 0.85 | 0.69 | 104.51 |
| 16 | transaction | 27.23 | 2.06 | 1.94 | 101.42 |

## Sampled peak server RSS (MiB)

| Workers | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | point | 444.00 | 453.40 | 143.80 | 452.95 |
| 1 | mixed | 363.65 | 478.75 | 152.40 | 466.50 |
| 1 | transaction | 368.45 | 523.05 | 152.85 | 511.55 |
| 8 | point | 392.85 | 594.15 | 153.25 | 551.30 |
| 8 | mixed | 406.00 | 594.60 | 154.75 | 599.10 |
| 8 | transaction | 413.60 | 595.10 | 155.10 | 637.05 |
| 16 | point | 401.75 | 611.00 | 157.00 | 716.75 |
| 16 | mixed | 394.70 | 611.75 | 165.10 | 634.25 |
| 16 | transaction | 560.20 | 611.75 | 164.10 | 552.10 |
