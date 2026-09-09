<!--
sha: baa1b51
date: 2026-09-09T03:07:39Z
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
| Method                          | Target | Mean        | Error        | StdDev     | Gen0   | Allocated |
|-------------------------------- |------- |------------:|-------------:|-----------:|-------:|----------:|
| **PointSelectByPk**                 | **fsdb**   |   **278.44 μs** |   **121.188 μs** |   **6.643 μs** |      **-** |     **881 B** |
| **PointSelectByPk**                 | **mysql**  |    **39.85 μs** |     **2.433 μs** |   **0.133 μs** | **0.0610** |     **880 B** |
| **PointSelectByConstantExpression** | **fsdb**   | **3,262.72 μs** | **2,054.358 μs** | **112.606 μs** |      **-** |     **917 B** |
| **PointSelectByConstantExpression** | **mysql**  |    **47.53 μs** |   **224.795 μs** |  **12.322 μs** | **0.0610** |     **912 B** |
