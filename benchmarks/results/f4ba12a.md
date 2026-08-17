<!--
sha: f4ba12a
date: 2026-08-17T10:01:52Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.107
fsdb server mode: in-memory (no --data-dir, no WAL/fsync)
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.107
  [Host]     : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD
  Job-WSWWFJ : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method               | Target | Mean         | Error        | StdDev       | Median       | Gen0   | Allocated |
|--------------------- |------- |-------------:|-------------:|-------------:|-------------:|-------:|----------:|
| **PointSelectByPk**      | **fsdb**   |    **129.97 μs** |    **17.288 μs** |     **6.165 μs** |    **129.50 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb   |  6,258.65 μs |   568.053 μs |   202.573 μs |  6,195.93 μs |      - |     568 B |
| InsertSingle         | fsdb   |    549.14 μs |   229.423 μs |    81.814 μs |    551.36 μs |      - |    1240 B |
| InsertBatch100       | fsdb   | 10,074.29 μs | 3,597.078 μs | 1,282.753 μs | 10,013.86 μs | 7.8125 |   96771 B |
| UpdateSingleRow      | fsdb   |    662.32 μs |    99.908 μs |    25.946 μs |    671.53 μs |      - |     680 B |
| JoinUsersOrders      | fsdb   | 10,742.98 μs |   735.197 μs |   262.178 μs | 10,704.42 μs |      - |     825 B |
| GroupByAggregate     | fsdb   | 26,070.25 μs | 1,167.662 μs |   416.399 μs | 26,070.21 μs |      - |     562 B |
| JsonExtract          | fsdb   |    224.56 μs |     9.182 μs |     2.385 μs |    224.49 μs |      - |     504 B |
| PreparedPointSelect  | fsdb   |    101.24 μs |     7.616 μs |     2.716 μs |     99.91 μs |      - |     960 B |
| **PointSelectByPk**      | **mysql**  |     **38.95 μs** |     **1.580 μs** |     **0.563 μs** |     **38.96 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit | mysql  |  1,938.47 μs |    79.241 μs |    28.258 μs |  1,929.44 μs |      - |     642 B |
| InsertSingle         | mysql  |    259.61 μs |   532.850 μs |   190.019 μs |    165.05 μs | 0.1221 |    1240 B |
| InsertBatch100       | mysql  |  1,226.59 μs |   500.170 μs |   178.365 μs |  1,151.84 μs | 9.7656 |   96712 B |
| UpdateSingleRow      | mysql  |    110.00 μs |    17.832 μs |     6.359 μs |    111.16 μs |      - |     743 B |
| JoinUsersOrders      | mysql  |    267.93 μs |     5.633 μs |     2.009 μs |    269.03 μs |      - |     889 B |
| GroupByAggregate     | mysql  | 20,448.83 μs |   765.439 μs |   198.782 μs | 20,468.40 μs |      - |     634 B |
| JsonExtract          | mysql  |     62.26 μs |     7.271 μs |     2.593 μs |     61.00 μs | 0.0610 |     584 B |
| PreparedPointSelect  | mysql  |     32.42 μs |     3.388 μs |     1.208 μs |     32.88 μs | 0.0610 |     960 B |
