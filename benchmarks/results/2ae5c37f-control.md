<!--
sha: 2ae5c37f
date: 2026-10-06T19:53:03Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Native same-host control repeat. Command:
`FSDB_BENCH_METHODS=PointSelectByPk just bench-quick`.

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method          | Target | Mean      | Error     | StdDev    | Allocated |
|---------------- |------- |----------:|----------:|----------:|----------:|
| **PointSelectByPk** | **fsdb**   | **512.38 μs** | **212.49 μs** | **11.647 μs** |     **881 B** |
| **PointSelectByPk** | **mysql**  |  **77.93 μs** |  **23.31 μs** |  **1.278 μs** |     **880 B** |
