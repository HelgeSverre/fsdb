<!--
sha: de493f5d
date: 2026-10-14T12:24:30Z
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
| **PointSelectByPk**           | **fsdb**   | **372.03 μs** | **637.84 μs** | **34.962 μs** |      **-** |     **881 B** |
| **PointSelectByPk**           | **mysql**  |  **44.55 μs** | **188.49 μs** | **10.332 μs** | **0.0610** |     **880 B** |
| **CorrelatedDerivedEquality** | **fsdb**   | **949.43 μs** | **126.65 μs** |  **6.942 μs** |      **-** |     **777 B** |
| CorrelatedCteEquality     | fsdb   | 813.61 μs | 185.20 μs | 10.151 μs |      - |     841 B |
| **CorrelatedDerivedEquality** | **mysql**  | **221.06 μs** | **118.63 μs** |  **6.503 μs** |      **-** |     **776 B** |
| CorrelatedCteEquality     | mysql  | 215.62 μs |  35.27 μs |  1.933 μs |      - |     840 B |
