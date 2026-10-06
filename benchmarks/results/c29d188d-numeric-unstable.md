<!--
sha: c29d188d
date: 2026-10-06T16:32:26Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Status: discarded for performance conclusions. The subsequent point-query
check changed from 430.00 to 302.57 microseconds for fsdb and from 66.86 to
40.80 microseconds for MySQL, exceeding the 20% repeatability threshold.
Retained as raw evidence of machine/run variability.

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                     | Target | Mean         | Error        | StdDev     | Gen0   | Allocated |
|--------------------------- |------- |-------------:|-------------:|-----------:|-------:|----------:|
| **LiteralNumericExpressions**  | **fsdb**   |    **168.74 μs** |    **13.639 μs** |   **0.748 μs** |      **-** |     **568 B** |
| ScalarLiteralSubqueries    | fsdb   |    191.56 μs |     6.040 μs |   0.331 μs |      - |     536 B |
| TextNumericAggregates      | fsdb   | 25,522.24 μs | 7,620.026 μs | 417.679 μs |      - |     562 B |
| PreparedNumericExpressions | fsdb   |    163.28 μs |    82.778 μs |   4.537 μs |      - |     984 B |
| **LiteralNumericExpressions**  | **mysql**  |     **35.12 μs** |    **18.773 μs** |   **1.029 μs** | **0.0610** |     **648 B** |
| ScalarLiteralSubqueries    | mysql  |     44.61 μs |     4.687 μs |   0.257 μs | 0.0610 |     536 B |
| TextNumericAggregates      | mysql  |  2,698.45 μs |   453.990 μs |  24.885 μs |      - |     524 B |
| PreparedNumericExpressions | mysql  |     44.15 μs |    29.395 μs |   1.611 μs | 0.0916 |     984 B |
| **PointSelectByPk**            | **fsdb**   |    **430.00 μs** |   **177.981 μs** |   **9.756 μs** |      **-** |     **881 B** |
| **PointSelectByPk**            | **mysql**  |     **66.86 μs** |     **5.925 μs** |   **0.325 μs** |      **-** |     **880 B** |
