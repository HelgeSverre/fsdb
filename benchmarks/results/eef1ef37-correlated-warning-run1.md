<!--
sha: eef1ef37
date: 2026-11-08T17:48:30Z
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
  Job-AGDWNM : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method                     | Target | Mean        | Error     | StdDev     | Allocated |
|--------------------------- |------- |------------:|----------:|-----------:|----------:|
| **PointSelectByPk**            | **fsdb**   | **1,113.39 μs** | **508.66 μs** | **181.394 μs** |     **881 B** |
| **PointSelectByPk**            | **mysql**  |    **53.72 μs** |  **13.04 μs** |   **3.386 μs** |     **880 B** |
| **CorrelatedPhysicalExists**   | **fsdb**   | **1,864.02 μs** | **750.81 μs** | **267.745 μs** |     **507 B** |
| CorrelatedPhysicalEquality | fsdb   | 1,827.47 μs | 564.14 μs | 201.176 μs |     732 B |
| CorrelatedOrderCount       | fsdb   | 1,661.78 μs | 694.93 μs | 247.818 μs |     504 B |
| **CorrelatedPhysicalExists**   | **mysql**  |   **479.63 μs** | **386.43 μs** | **137.806 μs** |     **504 B** |
| CorrelatedPhysicalEquality | mysql  |   539.41 μs | 162.04 μs |  57.786 μs |     729 B |
| CorrelatedOrderCount       | mysql  |   618.48 μs | 253.17 μs |  90.283 μs |     506 B |
