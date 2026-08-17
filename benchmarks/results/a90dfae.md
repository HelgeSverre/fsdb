<!--
sha: a90dfae
date: 2026-08-17T05:19:41Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.107
fsdb server mode: in-memory (no --data-dir, no WAL/fsync)
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.107
  [Host]     : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD
  Job-BMMDKB : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method               | Target | Mean          | Error         | StdDev       | Gen0   | Allocated |
|--------------------- |------- |--------------:|--------------:|-------------:|-------:|----------:|
| **PointSelectByPk**      | **fsdb**   |     **102.75 μs** |      **0.988 μs** |     **0.352 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb   |  15,804.64 μs |    495.731 μs |   128.740 μs |      - |     594 B |
| InsertSingle         | fsdb   |     492.20 μs |    174.728 μs |    62.310 μs |      - |    1240 B |
| InsertBatch100       | fsdb   |   8,582.01 μs |  3,471.164 μs | 1,237.851 μs | 7.8125 |   96656 B |
| UpdateSingleRow      | fsdb   |   2,330.84 μs |     37.857 μs |    13.500 μs |      - |     683 B |
| JoinUsersOrders      | fsdb   | 200,799.97 μs | 10,744.181 μs | 3,831.479 μs |      - |         - |
| GroupByAggregate     | fsdb   |  21,774.07 μs |    665.010 μs |   237.149 μs |      - |     544 B |
| JsonExtract          | fsdb   |   9,380.30 μs |    137.050 μs |    35.591 μs |      - |     521 B |
| PreparedPointSelect  | fsdb   |      90.09 μs |      1.698 μs |     0.441 μs |      - |     960 B |
| **PointSelectByPk**      | **mysql**  |      **38.03 μs** |      **0.515 μs** |     **0.184 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit | mysql  |   1,826.65 μs |      8.160 μs |     2.910 μs |      - |     642 B |
| InsertSingle         | mysql  |     104.72 μs |     27.444 μs |     9.787 μs | 0.1221 |    1240 B |
| InsertBatch100       | mysql  |   1,218.10 μs |    293.121 μs |   104.530 μs | 9.7656 |   96714 B |
| UpdateSingleRow      | mysql  |     125.16 μs |     25.615 μs |     9.135 μs |      - |     743 B |
| JoinUsersOrders      | mysql  |     239.95 μs |      0.737 μs |     0.192 μs |      - |     888 B |
| GroupByAggregate     | mysql  |  18,871.12 μs |    395.629 μs |   141.085 μs |      - |     634 B |
| JsonExtract          | mysql  |      58.04 μs |      0.478 μs |     0.171 μs | 0.0610 |     584 B |
| PreparedPointSelect  | mysql  |      32.60 μs |      0.207 μs |     0.074 μs | 0.0610 |     960 B |
