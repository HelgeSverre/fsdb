# Integral DOUBLE constant-bound profile

<!--
code sha: 177f6306
date: 2026-10-10T02:39:28Z
host: Darwin 24.6.0 arm64, Apple M2 Max
dotnet: 10.0.401
-->

`dotnet build src/Fsdb -c Release` followed by two fresh processes running
`dotnet fsi --nologo benchmarks/scripts/constant-bound-profile.fsx`.
Each process creates an in-memory fsdb table with 20,000 integer primary keys.
The queries return the same single row and include SQL parsing and query
handling, but no wire protocol. `EXPLAIN` confirms `const` for
`id=IF(1,ABS('15000'),3)` and `ALL` for
`id+0=IF(1,ABS('15000'),3)`. Each query gets 20 untimed warmups; each sample
starts after a full GC. The first two samples are excluded from the medians
to reduce JIT effects.

| Fresh process | Indexed median | Forced-scan median | Scan / indexed | Indexed allocation/query | Scan allocation/query |
|---|---:|---:|---:|---:|---:|
| 1 | 0.102 ms | 45.644 ms | 447× | 265 KB | 100 MB |
| 2 | 0.100 ms | 46.370 ms | 465× | 265 KB | 100 MB |

The indexed medians differ by 2.5% across processes. This is a directional
in-process comparison of these two SQL shapes, not a MySQL-vs-fsdb result or
a before/after regression measurement. The scan shape also does `id+0`
arithmetic, so the ratio is not a pure index-implementation speedup.

Raw samples (`elapsed_ms` and `allocated_bytes` cover all iterations):

```csv
process,case,trial,iterations,elapsed_ms,allocated_bytes
1,indexed,1,200,34.222,53689944
1,scan,1,10,464.445,1004195840
1,indexed,2,200,23.650,53490400
1,scan,2,10,466.986,1004179080
1,indexed,3,200,21.004,53111280
1,scan,3,10,456.436,1004172960
1,indexed,4,200,20.933,53090080
1,scan,4,10,454.547,1004173208
1,indexed,5,200,19.948,53090080
1,scan,5,10,455.405,1004173136
1,indexed,6,200,20.432,53090080
1,scan,6,10,481.393,1004173712
1,indexed,7,200,19.857,53090080
1,scan,7,10,484.085,1004174456
2,indexed,1,200,32.828,53689944
2,scan,1,10,453.276,1004196240
2,indexed,2,200,24.850,53512352
2,scan,2,10,456.894,1004179832
2,indexed,3,200,21.079,53116608
2,scan,3,10,450.445,1004172960
2,indexed,4,200,19.876,53090080
2,scan,4,10,463.704,1004172960
2,indexed,5,200,20.040,53090080
2,scan,5,10,457.420,1004173136
2,indexed,6,200,19.554,53090080
2,scan,6,10,465.504,1004173216
2,indexed,7,200,19.936,53090080
2,scan,7,10,473.830,1004173960
```
