<!--
sha: 4d6b49d2
date: 2026-11-08T17:50:58Z
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
  Job-CROELW : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method                     | Target | Mean      | Error     | StdDev    | Allocated |
|--------------------------- |------- |----------:|----------:|----------:|----------:|
| **PointSelectByPk**            | **fsdb**   | **349.31 μs** | **21.108 μs** |  **7.527 μs** |     **881 B** |
| **PointSelectByPk**            | **mysql**  |  **38.31 μs** |  **5.697 μs** |  **2.032 μs** |     **880 B** |
| **CorrelatedPhysicalExists**   | **fsdb**   | **638.47 μs** | **20.536 μs** |  **7.323 μs** |     **505 B** |
| CorrelatedPhysicalEquality | fsdb   | 761.88 μs | 17.831 μs |  4.631 μs |     729 B |
| CorrelatedOrderCount       | fsdb   | 622.04 μs | 41.901 μs | 14.942 μs |     505 B |
| **CorrelatedPhysicalExists**   | **mysql**  | **169.38 μs** |  **1.758 μs** |  **0.627 μs** |     **504 B** |
| CorrelatedPhysicalEquality | mysql  | 206.11 μs | 20.580 μs |  5.345 μs |     728 B |
| CorrelatedOrderCount       | mysql  | 206.26 μs |  3.552 μs |  0.923 μs |     504 B |
