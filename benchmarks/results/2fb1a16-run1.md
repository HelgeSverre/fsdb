<!--
sha: 2fb1a16
date: 2026-08-17T09:19:53Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.107
fsdb server mode: in-memory (no --data-dir, no WAL/fsync)
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.107
  [Host]     : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD
  Job-NITARS : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method               | Target | Mean         | Error        | StdDev       | Gen0   | Allocated |
|--------------------- |------- |-------------:|-------------:|-------------:|-------:|----------:|
| **PointSelectByPk**      | **fsdb**   |    **110.64 μs** |     **4.153 μs** |     **1.078 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb   | 10,704.20 μs |   205.017 μs |    53.242 μs |      - |     577 B |
| InsertSingle         | fsdb   |    537.94 μs |   205.940 μs |    73.440 μs |      - |    1241 B |
| InsertBatch100       | fsdb   |  9,807.69 μs | 4,746.278 μs | 1,692.569 μs | 7.8125 |   96767 B |
| UpdateSingleRow      | fsdb   |    574.82 μs |    23.847 μs |     8.504 μs |      - |     680 B |
| JoinUsersOrders      | fsdb   |  9,376.35 μs |   201.695 μs |    71.927 μs |      - |     825 B |
| GroupByAggregate     | fsdb   | 25,831.06 μs | 2,004.450 μs |   714.806 μs |      - |     972 B |
| JsonExtract          | fsdb   |    210.56 μs |     5.925 μs |     2.113 μs |      - |     504 B |
| PreparedPointSelect  | fsdb   |     99.37 μs |     6.701 μs |     1.740 μs |      - |     960 B |
| **PointSelectByPk**      | **mysql**  |     **40.36 μs** |     **0.878 μs** |     **0.228 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit | mysql  |  1,913.63 μs |    15.011 μs |     5.353 μs |      - |     642 B |
| InsertSingle         | mysql  |    108.83 μs |    23.086 μs |     8.233 μs | 0.1221 |    1240 B |
| InsertBatch100       | mysql  |  1,088.54 μs |    58.133 μs |    15.097 μs | 9.7656 |   96714 B |
| UpdateSingleRow      | mysql  |    112.29 μs |     5.787 μs |     1.503 μs |      - |     743 B |
| JoinUsersOrders      | mysql  |    257.82 μs |     5.543 μs |     1.440 μs |      - |     889 B |
| GroupByAggregate     | mysql  | 19,958.71 μs |   270.671 μs |    70.292 μs |      - |     634 B |
| JsonExtract          | mysql  |     62.88 μs |     1.238 μs |     0.441 μs |      - |     584 B |
| PreparedPointSelect  | mysql  |     35.92 μs |     4.977 μs |     1.775 μs | 0.0610 |     960 B |
