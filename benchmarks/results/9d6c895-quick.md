<!--
sha: 9d6c895
date: 2026-09-09T08:34:45Z
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
| Method                      | Target | Mean       | Error     | StdDev   | Allocated |
|---------------------------- |------- |-----------:|----------:|---------:|----------:|
| **CountIndexedConjunction**     | **fsdb**   |   **335.0 μs** |  **85.10 μs** |  **4.66 μs** |     **873 B** |
| CountSingleIndexConjunction | fsdb   |   337.5 μs | 189.19 μs | 10.37 μs |     881 B |
| CountConjunctionScan        | fsdb   | 3,561.2 μs | 485.12 μs | 26.59 μs |     893 B |
| **CountIndexedConjunction**     | **mysql**  |   **141.3 μs** |   **4.92 μs** |  **0.27 μs** |     **872 B** |
| CountSingleIndexConjunction | mysql  |   146.6 μs | 198.54 μs | 10.88 μs |     880 B |
| CountConjunctionScan        | mysql  | 1,342.0 μs |  47.08 μs |  2.58 μs |     970 B |
