<!--
sha: d28e4646
date: 2026-11-05T09:07:21Z
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
| Method                           | Target | Mean      | Error      | StdDev    | Allocated |
|--------------------------------- |------- |----------:|-----------:|----------:|----------:|
| **CorrelatedEqualityOuterOnly**      | **fsdb**   | **359.36 μs** | **167.594 μs** |  **9.186 μs** |     **505 B** |
| CorrelatedScalarNoSource         | fsdb   | 604.03 μs | 677.488 μs | 37.135 μs |     505 B |
| CorrelatedScalarNoSourceFiltered | fsdb   | 604.95 μs | 193.668 μs | 10.616 μs |     505 B |
| CorrelatedPhysicalExists         | fsdb   | 687.84 μs | 174.541 μs |  9.567 μs |     505 B |
| **CorrelatedEqualityOuterOnly**      | **mysql**  |  **75.75 μs** |   **8.277 μs** |  **0.454 μs** |     **504 B** |
| CorrelatedScalarNoSource         | mysql  | 130.86 μs | 996.326 μs | 54.612 μs |     504 B |
| CorrelatedScalarNoSourceFiltered | mysql  |  91.40 μs |  44.612 μs |  2.445 μs |     504 B |
| CorrelatedPhysicalExists         | mysql  | 173.67 μs |  15.250 μs |  0.836 μs |     504 B |
