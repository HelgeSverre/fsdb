<!--
sha: a90dfae
date: 2026-08-17T05:12:53Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.107
fsdb server mode: in-memory (no --data-dir, no WAL/fsync)
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.107
  [Host]     : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD
  Job-JUPEXH : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method               | Target | Mean          | Error         | StdDev       | Gen0   | Allocated |
|--------------------- |------- |--------------:|--------------:|-------------:|-------:|----------:|
| **PointSelectByPk**      | **fsdb**   |     **102.71 μs** |      **1.214 μs** |     **0.315 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb   |  15,945.22 μs |    510.284 μs |   181.972 μs |      - |     594 B |
| InsertSingle         | fsdb   |     490.12 μs |    171.415 μs |    61.128 μs |      - |    1241 B |
| InsertBatch100       | fsdb   |   8,720.16 μs |  3,955.808 μs | 1,410.679 μs | 7.8125 |   96656 B |
| UpdateSingleRow      | fsdb   |   2,341.14 μs |     52.697 μs |    13.685 μs |      - |     683 B |
| JoinUsersOrders      | fsdb   | 190,714.42 μs | 15,661.824 μs | 5,585.157 μs |      - |         - |
| GroupByAggregate     | fsdb   |  21,919.11 μs |    218.185 μs |    77.807 μs |      - |     554 B |
| JsonExtract          | fsdb   |   9,434.09 μs |    140.099 μs |    49.961 μs |      - |     521 B |
| PreparedPointSelect  | fsdb   |      88.99 μs |      1.176 μs |     0.419 μs |      - |     960 B |
| **PointSelectByPk**      | **mysql**  |      **38.01 μs** |      **0.454 μs** |     **0.162 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit | mysql  |   1,823.77 μs |     17.423 μs |     6.213 μs |      - |     642 B |
| InsertSingle         | mysql  |     113.89 μs |     13.711 μs |     3.561 μs | 0.1221 |    1240 B |
| InsertBatch100       | mysql  |   1,084.20 μs |    241.194 μs |    62.637 μs | 9.7656 |   96714 B |
| UpdateSingleRow      | mysql  |     107.22 μs |     17.932 μs |     6.395 μs |      - |     743 B |
| JoinUsersOrders      | mysql  |     236.42 μs |      2.332 μs |     0.832 μs |      - |     888 B |
| GroupByAggregate     | mysql  |  18,753.23 μs |    116.962 μs |    41.710 μs |      - |     634 B |
| JsonExtract          | mysql  |      57.75 μs |      0.498 μs |     0.178 μs | 0.0610 |     584 B |
| PreparedPointSelect  | mysql  |      32.79 μs |      0.622 μs |     0.222 μs | 0.0610 |     960 B |
