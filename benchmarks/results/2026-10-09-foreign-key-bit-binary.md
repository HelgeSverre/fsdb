# BIT/binary foreign-key update snapshot

Measured at `e1123f2d` on 2026-10-09 with the new
`ForeignKeyBenchmarks` workload, BenchmarkDotNet 0.14.0 ShortRun (three warm-up
and three measured iterations), .NET 10.0.12, and native loopback servers on
an Apple M2 Max running macOS 15.6. The fixture has 1,000 `BIT(16)` parent
keys and one child row. Each operation alternates that child's reference
between parent keys 1 and 2. One child has a same-type `BIT(16)` reference;
the other has a byte-compatible `VARBINARY(2)` reference. Both servers use
the same schema and SQL. The command was:

```sh
FSDB_BENCH_METHODS=BitUpdate,BinaryUpdate FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=5000 FSDB_BENCH_ARTICLES=1000 just _bench-run --quick
```

| Child reference | fsdb mean (range) | MySQL mean (range) |
|---|---:|---:|
| `BIT(16)` | 409 µs (283–564) | 181 µs (177–189) |
| `VARBINARY(2)` | 320 µs (267–403) | 374 µs (217–503) |

This run is too noisy to establish a mixed-type penalty or a server ranking:
the ranges overlap within each server, and each case has only three measured
iterations. The host was running other substantial work. Fsdb used its
in-memory mode while MySQL used normal durable writes, so the cross-server
write times do not match durability. The useful result is a repeatable,
same-schema workload for the recently corrected byte-key path. A quieter,
durability-matched run with larger parent sets is needed before optimizing
foreign-key lookup from these figures.
