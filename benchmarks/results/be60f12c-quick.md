<!--
sha: be60f12c
date: 2026-10-11T01:13:41Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 1000 users, 5000 orders, 1000 articles
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
| Method                        | Target | Mean        | Error        | StdDev     | Gen0   | Allocated |
|------------------------------ |------- |------------:|-------------:|-----------:|-------:|----------:|
| **NullRejectedLeftJoinIndexedOr** | **fsdb**   | **4,626.26 μs** | **2,108.138 μs** | **115.554 μs** |      **-** |     **731 B** |
| **NullRejectedLeftJoinIndexedOr** | **mysql**  |    **51.82 μs** |     **4.957 μs** |   **0.272 μs** | **0.0610** |     **720 B** |
| **PointSelectByPk**               | **fsdb**   |   **166.88 μs** |    **25.862 μs** |   **1.418 μs** |      **-** |     **881 B** |
| **PointSelectByPk**               | **mysql**  |    **38.67 μs** |     **7.163 μs** |   **0.393 μs** | **0.0610** |     **880 B** |
