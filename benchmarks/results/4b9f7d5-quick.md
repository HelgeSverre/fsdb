<!--
sha: 4b9f7d5
date: 2026-09-09T09:22:02Z
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
| Method                          | Target | Mean         | Error         | StdDev     | Gen0   | Allocated |
|-------------------------------- |------- |-------------:|--------------:|-----------:|-------:|----------:|
| **FilterByComposedFunctionalIndex** | **fsdb**   |    **275.62 μs** |    **566.704 μs** |  **31.063 μs** |      **-** |     **776 B** |
| FilterByComposedFunctionalScan  | fsdb   | 37,223.08 μs |  7,068.931 μs | 387.472 μs |      - |     897 B |
| OrderByComposedFunctionalIndex  | fsdb   |    183.93 μs |     32.730 μs |   1.794 μs |      - |     488 B |
| OrderByComposedFunctionalScan   | fsdb   | 31,947.45 μs | 16,109.001 μs | 882.989 μs |      - |     573 B |
| **FilterByComposedFunctionalIndex** | **mysql**  |     **48.92 μs** |      **8.362 μs** |   **0.458 μs** | **0.0610** |     **775 B** |
| FilterByComposedFunctionalScan  | mysql  |  2,296.62 μs |    275.528 μs |  15.103 μs |      - |     883 B |
| OrderByComposedFunctionalIndex  | mysql  |     58.28 μs |     45.213 μs |   2.478 μs |      - |     488 B |
| OrderByComposedFunctionalScan   | mysql  |  2,305.38 μs |    211.368 μs |  11.586 μs |      - |     572 B |
