<!--
sha: 35654c02
variant: numeric CHAR casts use direct text conversion after display formatting
date: 2026-10-10T05:32:16Z
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
| Method                | Target | Mean        | Error         | StdDev      | Allocated |
|---------------------- |------- |------------:|--------------:|------------:|----------:|
| **TextNumericAggregates** | **fsdb**   | **55,003.0 μs** | **122,271.06 μs** | **6,702.09 μs** |         **-** |
| SumAge                | fsdb   |  3,713.9 μs |   8,580.63 μs |   470.33 μs |     488 B |
| SumCastAgeAsChar      | fsdb   | 13,106.2 μs |  36,236.36 μs | 1,986.24 μs |     530 B |
| **TextNumericAggregates** | **mysql**  |  **2,276.4 μs** |     **372.53 μs** |    **20.42 μs** |     **524 B** |
| SumAge                | mysql  |    958.6 μs |      30.92 μs |     1.69 μs |     490 B |
| SumCastAgeAsChar      | mysql  |  1,143.0 μs |     163.64 μs |     8.97 μs |     490 B |
