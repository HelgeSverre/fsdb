<!--
sha: 2cf93f5d
date: 2026-10-12T17:57:14Z
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
| Method                 | Target | Mean         | Error       | StdDev    | Allocated |
|----------------------- |------- |-------------:|------------:|----------:|----------:|
| **FilterByLikePrefix**     | **fsdb**   |    **691.44 μs** |   **199.63 μs** |  **10.94 μs** |     **488 B** |
| FilterByLikePrefixScan | fsdb   | 21,101.79 μs | 3,766.20 μs | 206.44 μs |     530 B |
| **FilterByLikePrefix**     | **mysql**  |     **85.07 μs** |   **191.96 μs** |  **10.52 μs** |     **488 B** |
| FilterByLikePrefixScan | mysql  |  1,644.74 μs |   799.58 μs |  43.83 μs |     568 B |
