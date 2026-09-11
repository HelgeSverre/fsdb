<!--
sha: ff58fd4c
date: 2026-09-11T00:28:50Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
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
| Method                              | Target | Mean            | Error            | StdDev        | Gen0   | Allocated |
|------------------------------------ |------- |----------------:|-----------------:|--------------:|-------:|----------:|
| **FilterByComposedFunctionalRange**     | **fsdb**   |     **5,852.59 μs** |     **2,333.902 μs** |    **127.929 μs** |      **-** |    **1002 B** |
| FilterByComposedFunctionalRangeScan | fsdb   |    52,896.42 μs |    71,944.136 μs |  3,943.501 μs |      - |    1146 B |
| CorrelatedFunctionalTextRange       | fsdb   |   193,681.45 μs |    10,990.738 μs |    602.439 μs |      - |         - |
| CorrelatedFunctionalTextRangeScan   | fsdb   | 8,607,810.62 μs | 1,105,950.795 μs | 60,620.895 μs |      - |         - |
| **FilterByComposedFunctionalRange**     | **mysql**  |        **53.35 μs** |         **6.911 μs** |      **0.379 μs** | **0.0610** |     **991 B** |
| FilterByComposedFunctionalRangeScan | mysql  |     2,770.74 μs |       340.003 μs |     18.637 μs |      - |    1123 B |
| CorrelatedFunctionalTextRange       | mysql  |   359,814.10 μs |     9,111.480 μs |    499.431 μs |      - |         - |
| CorrelatedFunctionalTextRangeScan   | mysql  |   330,051.63 μs |    10,758.068 μs |    589.686 μs |      - |         - |
