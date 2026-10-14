<!--
sha: f157d684
date: 2026-10-14T07:41:25Z
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
| Method                 | Target | Mean      | Error      | StdDev   | Gen0   | Allocated |
|----------------------- |------- |----------:|-----------:|---------:|-------:|----------:|
| **PointSelectByFixedPk**   | **fsdb**   | **207.20 μs** | **173.712 μs** | **9.522 μs** |      **-** |     **592 B** |
| PointSelectByCastBound | fsdb   | 213.72 μs |  97.247 μs | 5.330 μs |      - |     592 B |
| **PointSelectByFixedPk**   | **mysql**  |  **33.47 μs** |   **4.146 μs** | **0.227 μs** | **0.0610** |     **592 B** |
| PointSelectByCastBound | mysql  |  34.24 μs |   6.427 μs | 0.352 μs | 0.0610 |     592 B |
