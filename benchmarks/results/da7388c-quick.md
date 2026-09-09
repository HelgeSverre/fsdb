<!--
sha: da7388c
date: 2026-09-09T09:33:59Z
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
| Method                          | Target | Mean         | Error        | StdDev     | Gen0   | Allocated |
|-------------------------------- |------- |-------------:|-------------:|-----------:|-------:|----------:|
| **FilterByComposedFunctionalIndex** | **fsdb**   |    **259.21 μs** |    **28.575 μs** |   **1.566 μs** |      **-** |     **793 B** |
| FilterByComposedFunctionalScan  | fsdb   | 35,734.26 μs | 8,911.928 μs | 488.493 μs |      - |     913 B |
| OrderByComposedFunctionalIndex  | fsdb   |    191.38 μs |    30.371 μs |   1.665 μs |      - |     488 B |
| OrderByComposedFunctionalScan   | fsdb   | 30,601.76 μs | 1,379.481 μs |  75.614 μs |      - |     530 B |
| **FilterByComposedFunctionalIndex** | **mysql**  |     **46.12 μs** |     **3.331 μs** |   **0.183 μs** | **0.0610** |     **792 B** |
| FilterByComposedFunctionalScan  | mysql  |  2,208.21 μs |   201.173 μs |  11.027 μs |      - |     900 B |
| OrderByComposedFunctionalIndex  | mysql  |     54.51 μs |     1.519 μs |   0.083 μs |      - |     488 B |
| OrderByComposedFunctionalScan   | mysql  |  2,254.91 μs |    73.059 μs |   4.005 μs |      - |     572 B |
