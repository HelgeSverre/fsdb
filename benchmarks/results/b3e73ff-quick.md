<!--
sha: b3e73ff
date: 2026-09-01T00:42:33Z
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
| Method                | Target | Mean       | Error     | StdDev  | Allocated |
|---------------------- |------- |-----------:|----------:|--------:|----------:|
| **FullTextNaturalSearch** | **fsdb**   | **1,311.1 μs** | **118.33 μs** | **6.49 μs** |     **507 B** |
| FullTextJoinUsers     | fsdb   | 2,182.5 μs |  93.58 μs | 5.13 μs |     525 B |
| **FullTextNaturalSearch** | **mysql**  |   **410.4 μs** |  **35.55 μs** | **1.95 μs** |     **505 B** |
| FullTextJoinUsers     | mysql  |   432.3 μs |   9.94 μs | 0.54 μs |     521 B |
