<!--
sha: ba86713
date: 2026-09-09T07:56:54Z
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
| Method                  | Target | Mean       | Error     | StdDev   | Allocated |
|------------------------ |------- |-----------:|----------:|---------:|----------:|
| **CountIndexedDisjunction** | **fsdb**   |   **400.0 μs** | **241.65 μs** | **13.25 μs** |     **872 B** |
| CountDisjunctionScan    | fsdb   | 5,855.9 μs | 267.24 μs | 14.65 μs |     898 B |
| **CountIndexedDisjunction** | **mysql**  |   **170.6 μs** |   **2.23 μs** |  **0.12 μs** |     **871 B** |
| CountDisjunctionScan    | mysql  | 1,547.1 μs | 454.11 μs | 24.89 μs |     969 B |
