<!--
commit: 8e904fd
targets: in-memory fsdb; durable MySQL 8.4
dataset: 10,000 articles
job: BenchmarkDotNet ShortRun, 3 warmups + 3 measured iterations
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                      | Target | Mean        | Error        | StdDev    | Allocated |
|---------------------------- |------- |------------:|-------------:|----------:|----------:|
| **FullTextNaturalSearch**       | **fsdb**   |  **2,762.0 μs** |    **426.08 μs** |  **23.36 μs** |     **509 B** |
| FullTextBooleanSearch       | fsdb   | 17,315.7 μs | 13,379.92 μs | 733.40 μs |     508 B |
| FullTextAccentSearch        | fsdb   |  1,617.6 μs |    455.45 μs |  24.96 μs |     507 B |
| FullTextBooleanPrefixSearch | fsdb   |  9,733.4 μs |  3,387.70 μs | 185.69 μs |     525 B |
| **FullTextNaturalSearch**       | **mysql**  |    **408.2 μs** |     **57.95 μs** |   **3.18 μs** |     **505 B** |
| FullTextBooleanSearch       | mysql  |  1,031.7 μs |     36.17 μs |   1.98 μs |     506 B |
| FullTextAccentSearch        | mysql  |    215.0 μs |    320.60 μs |  17.57 μs |     504 B |
| FullTextBooleanPrefixSearch | mysql  |    300.3 μs |    256.45 μs |  14.06 μs |     505 B |
