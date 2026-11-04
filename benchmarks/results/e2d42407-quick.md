<!--
sha: e2d42407
date: 2026-11-04T11:28:52Z
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
| Method                    | Target | Mean      | Error     | StdDev    | Gen0   | Allocated |
|-------------------------- |------- |----------:|----------:|----------:|-------:|----------:|
| **PointSelectByPk**           | **fsdb**   | **358.85 μs** |  **78.79 μs** |  **4.319 μs** |      **-** |     **880 B** |
| **PointSelectByPk**           | **mysql**  |  **43.12 μs** |  **15.99 μs** |  **0.877 μs** | **0.0610** |     **880 B** |
| **CorrelatedDerivedEquality** | **fsdb**   | **960.65 μs** | **781.77 μs** | **42.851 μs** |      **-** |     **777 B** |
| CorrelatedCteEquality     | fsdb   | 980.10 μs | 196.19 μs | 10.754 μs |      - |     843 B |
| **CorrelatedDerivedEquality** | **mysql**  | **233.91 μs** |  **87.07 μs** |  **4.773 μs** |      **-** |     **776 B** |
| CorrelatedCteEquality     | mysql  | 239.11 μs | 147.98 μs |  8.111 μs |      - |     840 B |
