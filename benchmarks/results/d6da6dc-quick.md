<!--
sha: d6da6dc
date: 2026-09-06T01:28:14Z
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
| Method                    | Target | Mean       | Error       | StdDev    | Allocated |
|-------------------------- |------- |-----------:|------------:|----------:|----------:|
| **CountHalfIndexedLiteralIn** | **fsdb**   | **6,532.5 μs** | **3,138.16 μs** | **172.01 μs** |     **784 B** |
| CountHalfLiteralInScan    | fsdb   | 1,962.4 μs |   263.66 μs |  14.45 μs |     784 B |
| **CountHalfIndexedLiteralIn** | **mysql**  |   **728.7 μs** |    **11.99 μs** |   **0.66 μs** |     **777 B** |
| CountHalfLiteralInScan    | mysql  | 1,295.9 μs |    94.77 μs |   5.19 μs |     866 B |
