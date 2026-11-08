<!--
sha: 4d6b49d2
date: 2026-11-08T17:46:01Z
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
  [Host]     : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD
  Job-IGEOYY : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method                     | Target | Mean        | Error       | StdDev     | Gen0   | Allocated |
|--------------------------- |------- |------------:|------------:|-----------:|-------:|----------:|
| **PointSelectByPk**            | **fsdb**   | **1,306.64 μs** |   **574.08 μs** | **204.722 μs** |      **-** |     **883 B** |
| **PointSelectByPk**            | **mysql**  |    **40.38 μs** |    **12.91 μs** |   **3.352 μs** | **0.0610** |     **880 B** |
| **CorrelatedPhysicalExists**   | **fsdb**   | **2,323.69 μs** |   **825.71 μs** | **294.455 μs** |      **-** |     **509 B** |
| CorrelatedPhysicalEquality | fsdb   | 2,415.21 μs | 1,014.27 μs | 361.699 μs |      - |     728 B |
| CorrelatedOrderCount       | fsdb   | 1,790.77 μs | 2,213.03 μs | 789.187 μs |      - |     512 B |
| **CorrelatedPhysicalExists**   | **mysql**  |   **662.31 μs** |   **296.45 μs** | **105.717 μs** |      **-** |     **504 B** |
| CorrelatedPhysicalEquality | mysql  |   624.97 μs |   151.85 μs |  39.434 μs |      - |     728 B |
| CorrelatedOrderCount       | mysql  |   561.74 μs |   416.89 μs | 148.666 μs |      - |     504 B |
