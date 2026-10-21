<!--
sha: f8c3bf74
date: 2026-10-21T00:25:17Z
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
| Method                    | Target | Mean        | Error       | StdDev    | Median      | Gen0   | Allocated |
|-------------------------- |------- |------------:|------------:|----------:|------------:|-------:|----------:|
| **PointSelectByPk**           | **fsdb**   |   **947.73 μs** | **8,362.41 μs** | **458.37 μs** | **1,134.35 μs** |      **-** |     **883 B** |
| **PointSelectByPk**           | **mysql**  |    **66.40 μs** |   **311.41 μs** |  **17.07 μs** |    **57.00 μs** | **0.0610** |     **880 B** |
| **CorrelatedDerivedEquality** | **fsdb**   | **1,000.02 μs** |   **331.18 μs** |  **18.15 μs** | **1,000.84 μs** |      **-** |     **777 B** |
| CorrelatedCteEquality     | fsdb   | 1,347.65 μs | 4,773.83 μs | 261.67 μs | 1,356.94 μs |      - |     843 B |
| **CorrelatedDerivedEquality** | **mysql**  |   **339.52 μs** | **2,804.57 μs** | **153.73 μs** |   **254.18 μs** |      **-** |     **777 B** |
| CorrelatedCteEquality     | mysql  |   314.33 μs | 2,155.27 μs | 118.14 μs |   256.12 μs |      - |     841 B |
