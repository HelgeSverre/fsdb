<!--
sha: 64e8d3ab
date: 2026-10-10T12:40:31Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
methods: PointSelectByPk, PointSelectByConstantExpression
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
| Method                          | Target | Mean      | Error      | StdDev   | Gen0   | Allocated |
|-------------------------------- |------- |----------:|-----------:|---------:|-------:|----------:|
| **PointSelectByPk**                 | **fsdb**   | **465.11 μs** | **179.168 μs** | **9.821 μs** |      **-** |     **881 B** |
| **PointSelectByPk**                 | **mysql**  |  **44.35 μs** |   **7.030 μs** | **0.385 μs** | **0.0610** |     **880 B** |
| **PointSelectByConstantExpression** | **fsdb**   | **470.59 μs** |  **51.703 μs** | **2.834 μs** |      **-** |     **913 B** |
| **PointSelectByConstantExpression** | **mysql**  |  **44.97 μs** |   **7.219 μs** | **0.396 μs** | **0.0610** |     **912 B** |
