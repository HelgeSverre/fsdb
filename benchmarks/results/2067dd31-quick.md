<!--
sha: 2067dd31
date: 2026-10-10T16:21:57Z
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
| Method                     | Target | Mean      | Error      | StdDev   | Gen0   | Allocated |
|--------------------------- |------- |----------:|-----------:|---------:|-------:|----------:|
| **PointSelectByFixedPk**       | **fsdb**   | **248.35 μs** | **100.449 μs** | **5.506 μs** |      **-** |     **593 B** |
| PointSelectByRoundPi       | fsdb   | 231.83 μs |  32.355 μs | 1.773 μs |      - |     592 B |
| PointSelectByBitCountBound | fsdb   | 225.58 μs |  48.855 μs | 2.678 μs |      - |     592 B |
| **PointSelectByFixedPk**       | **mysql**  |  **41.81 μs** |   **2.228 μs** | **0.122 μs** | **0.0610** |     **592 B** |
| PointSelectByRoundPi       | mysql  |  43.23 μs |   4.549 μs | 0.249 μs | 0.0610 |     592 B |
| PointSelectByBitCountBound | mysql  |  42.12 μs |   1.514 μs | 0.083 μs | 0.0610 |     592 B |
