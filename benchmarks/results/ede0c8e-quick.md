<!--
sha: ede0c8e
date: 2026-09-09T03:23:11Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                          | Target | Mean      | Error      | StdDev   | Gen0   | Allocated |
|-------------------------------- |------- |----------:|-----------:|---------:|-------:|----------:|
| **PointSelectByPk**                 | **fsdb**   | **276.25 μs** | **110.419 μs** | **6.052 μs** |      **-** |     **881 B** |
| **PointSelectByPk**                 | **mysql**  |  **39.81 μs** |   **4.746 μs** | **0.260 μs** | **0.0610** |     **880 B** |
| **PointSelectByConstantExpression** | **fsdb**   | **279.81 μs** | **127.267 μs** | **6.976 μs** |      **-** |     **913 B** |
| **PointSelectByConstantExpression** | **mysql**  |  **40.22 μs** |   **5.203 μs** | **0.285 μs** | **0.0610** |     **912 B** |
