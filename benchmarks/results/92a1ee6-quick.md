<!--
sha: 92a1ee6
date: 2026-08-31T11:08:58Z
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
| Method              | Target | Mean     | Error     | StdDev   | Allocated |
|-------------------- |------- |---------:|----------:|---------:|----------:|
| **LeftJoinUsersOrders** | **fsdb**   | **748.1 μs** | **298.03 μs** | **16.34 μs** |     **825 B** |
| **LeftJoinUsersOrders** | **mysql**  | **178.6 μs** |  **10.77 μs** |  **0.59 μs** |     **824 B** |
