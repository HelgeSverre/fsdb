<!--
sha: 6aaef020
date: 2026-10-10T08:29:47Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
methods: CountIndexedConjunction, CountSingleIndexConjunction, CountConjunctionScan
dataset: 10000 users, 50000 orders, 10000 articles
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                      | Target | Mean       | Error       | StdDev    | Allocated |
|---------------------------- |------- |-----------:|------------:|----------:|----------:|
| **CountIndexedConjunction**     | **fsdb**   |   **596.7 μs** |   **477.97 μs** |  **26.20 μs** |     **873 B** |
| CountSingleIndexConjunction | fsdb   |   602.3 μs |   123.51 μs |   6.77 μs |     881 B |
| CountConjunctionScan        | fsdb   | 6,564.9 μs | 3,994.11 μs | 218.93 μs |     899 B |
| **CountIndexedConjunction**     | **mysql**  |   **152.7 μs** |    **13.24 μs** |   **0.73 μs** |     **872 B** |
| CountSingleIndexConjunction | mysql  |   161.2 μs |     9.69 μs |   0.53 μs |     880 B |
| CountConjunctionScan        | mysql  | 1,571.6 μs |   434.81 μs |  23.83 μs |     970 B |
