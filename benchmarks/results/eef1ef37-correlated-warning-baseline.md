<!--
sha: eef1ef37
date: 2026-11-08T17:53:13Z
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
  Job-BQEXZO : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method                     | Target | Mean      | Error     | StdDev    | Gen0   | Allocated |
|--------------------------- |------- |----------:|----------:|----------:|-------:|----------:|
| **PointSelectByPk**            | **fsdb**   | **351.29 μs** | **51.498 μs** | **18.365 μs** |      **-** |     **881 B** |
| **PointSelectByPk**            | **mysql**  |  **39.69 μs** |  **0.470 μs** |  **0.168 μs** | **0.0610** |     **880 B** |
| **CorrelatedPhysicalExists**   | **fsdb**   | **672.72 μs** | **20.134 μs** |  **5.229 μs** |      **-** |     **505 B** |
| CorrelatedPhysicalEquality | fsdb   | 799.69 μs | 40.957 μs | 14.606 μs |      - |     729 B |
| CorrelatedOrderCount       | fsdb   | 576.08 μs |  4.301 μs |  1.117 μs |      - |     505 B |
| **CorrelatedPhysicalExists**   | **mysql**  | **169.00 μs** |  **2.935 μs** |  **0.762 μs** |      **-** |     **504 B** |
| CorrelatedPhysicalEquality | mysql  | 229.01 μs | 68.737 μs | 17.851 μs |      - |     728 B |
| CorrelatedOrderCount       | mysql  | 206.59 μs |  4.296 μs |  1.116 μs |      - |     504 B |
