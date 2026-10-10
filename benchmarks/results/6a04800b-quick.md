<!--
sha: 6a04800b
date: 2026-10-10T14:47:40Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
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
| Method                                  | Target | Mean         | Error         | StdDev     | Gen0   | Allocated |
|---------------------------------------- |------- |-------------:|--------------:|-----------:|-------:|----------:|
| **PointSelectIdByPk**                       | **fsdb**   |    **258.87 μs** |     **91.059 μs** |   **4.991 μs** |      **-** |     **705 B** |
| FilterByComposedFunctionalIndex         | fsdb   |    391.00 μs |     29.574 μs |   1.621 μs |      - |     793 B |
| PreparedFilterByComposedFunctionalIndex | fsdb   |    262.35 μs |     59.996 μs |   3.289 μs |      - |     369 B |
| FilterByComposedFunctionalScan          | fsdb   | 68,115.01 μs | 11,091.607 μs | 607.968 μs |      - |         - |
| **PointSelectIdByPk**                       | **mysql**  |     **36.78 μs** |      **3.706 μs** |   **0.203 μs** | **0.0610** |     **704 B** |
| FilterByComposedFunctionalIndex         | mysql  |     48.50 μs |      5.735 μs |   0.314 μs | 0.0610 |     792 B |
| PreparedFilterByComposedFunctionalIndex | mysql  |     41.82 μs |      4.074 μs |   0.223 μs |      - |     368 B |
| FilterByComposedFunctionalScan          | mysql  |  2,183.79 μs |     16.994 μs |   0.932 μs |      - |     900 B |

The composed functional index makes the fsdb lookup about 174 times faster than
the scan on this dataset (391 μs versus 68.1 ms). Prepared execution reduces
the indexed lookup to 262 μs, close to the 259 μs primary-key baseline. MySQL
takes 48.5 μs for the indexed lookup and 41.8 μs for its prepared form. This
points to shared request/statement overhead as the next area to profile, rather
than a functional-index-specific optimization. These are directional ShortRun
measurements; the fsdb target is in memory and the MySQL target is durable.
