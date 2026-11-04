<!--
sha: c61baded
date: 2026-11-04T11:31:42Z
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
| Method                          | Target | Mean      | Error     | StdDev    | Gen0   | Allocated |
|-------------------------------- |------- |----------:|----------:|----------:|-------:|----------:|
| **CorrelatedDerivedEquality**       | **fsdb**   | **976.96 μs** | **909.62 μs** | **49.859 μs** |      **-** |     **777 B** |
| CorrelatedDerivedEqualityOneRow | fsdb   | 218.07 μs |  48.54 μs |  2.660 μs |      - |     769 B |
| CorrelatedDerivedEqualityNoRows | fsdb   | 189.33 μs |  27.38 μs |  1.501 μs |      - |     632 B |
| **CorrelatedDerivedEquality**       | **mysql**  | **262.95 μs** | **376.03 μs** | **20.612 μs** |      **-** |     **776 B** |
| CorrelatedDerivedEqualityOneRow | mysql  |  65.75 μs |  45.92 μs |  2.517 μs |      - |     768 B |
| CorrelatedDerivedEqualityNoRows | mysql  |  49.94 μs |  16.10 μs |  0.882 μs | 0.0610 |     632 B |
