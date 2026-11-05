# Multi-database write checkpoint (2026-11-05)

The maintained `multidb` torture workload tests independent transaction
ledgers across databases on one fsdb server, with MySQL 8.4.11 as its oracle.
This is a current scheduling and correctness checkpoint after the earlier
qualified-foreign-key lock-scope fix, not an isolated group-commit benchmark.

Command:

```sh
torture/scripts/run.sh multidb --databases 4 --workers 8 --operations 40 --accounts 64 --hot-accounts 8 --rollback-every 7 --seed 203 --timeout-seconds 90
torture/scripts/run.sh multidb --databases 8 --workers 16 --operations 30 --accounts 64 --hot-accounts 8 --rollback-every 7 --seed 204 --timeout-seconds 90
```

Each database committed 275 transactions and rolled back 45, with zero
failed transactions. The workload's per-database ledger, balance, and version
checks passed, and no cross-database writes were observed. The one-database
baseline took 1,065 ms. Four databases together took 2,664 ms versus a
4,260 ms serial projection, a 0.63 ratio that narrowly passes the harness's
0.65 scaling threshold. Peak reported memory was 203.1 MiB.

At 8 databases × 16 writers × 30 transactions, every database committed 412
and rolled back 68 transactions, again with no failed transactions or ledger
divergence. The one-database baseline was 1,342 ms; multi-database wall time
was 17,644 ms versus a 10,736 ms serial projection (ratio 1.64). The harness
classified this run as `multidb_scaling_gap`. Peak reported memory was
205.7 MiB.

The [August follow-up](../../torture/findings/2026-08-17-multidb-concurrency-campaign.md)
recorded ratios of 0.49 at 4 × 8 and 0.96 at 8 × 16. These separate runs are
not matched performance comparisons. The high-fan-out result confirms a
current throughput ceiling despite correct concurrent writes, but does not
identify its cause or justify a new scheduling policy. Matched durable
wire-write throughput also remains open.
