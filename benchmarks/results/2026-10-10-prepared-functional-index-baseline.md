<!--
sha: 54d47699
date: 2026-10-10T04:39:20Z
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
| Method                                  | Target | Mean         | Error         | StdDev       | Gen0   | Allocated |
|---------------------------------------- |------- |-------------:|--------------:|-------------:|-------:|----------:|
| **PointSelectIdByPk**                       | **fsdb**   |    **268.89 μs** |    **204.661 μs** |    **11.218 μs** |      **-** |     **705 B** |
| FilterByComposedFunctionalIndex         | fsdb   |    384.95 μs |     85.231 μs |     4.672 μs |      - |     793 B |
| PreparedPointSelectIdByPk               | fsdb   |    240.83 μs |    128.693 μs |     7.054 μs |      - |     233 B |
| PreparedFilterByComposedFunctionalIndex | fsdb   | 48,578.38 μs | 81,345.337 μs | 4,458.812 μs |      - |         - |
| **PointSelectIdByPk**                       | **mysql**  |     **39.75 μs** |      **8.057 μs** |     **0.442 μs** | **0.0610** |     **704 B** |
| FilterByComposedFunctionalIndex         | mysql  |     50.06 μs |     13.262 μs |     0.727 μs | 0.0610 |     792 B |
| PreparedPointSelectIdByPk               | mysql  |     33.27 μs |      9.557 μs |     0.524 μs |      - |     232 B |
| PreparedFilterByComposedFunctionalIndex | mysql  |     42.50 μs |      1.628 μs |     0.089 μs |      - |     368 B |
