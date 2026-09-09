<!--
sha: b2b0bca
date: 2026-09-09T01:27:52Z
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
| Method                           | Target | Mean       | Error       | StdDev     | Allocated |
|--------------------------------- |------- |-----------:|------------:|-----------:|----------:|
| **CorrelatedDerivedCompositePrefix** | **fsdb**   | **147.241 ms** | **371.0681 ms** | **20.3395 ms** |         **-** |
| **CorrelatedDerivedCompositePrefix** | **mysql**  |   **3.024 ms** |   **0.1356 ms** |  **0.0074 ms** |     **892 B** |

For a before/after control, the same benchmark source was run with the
composite-prefix executor change reverted. fsdb averaged 212.754 ms in that
run, versus 147.241 ms above. This short run indicates a useful reduction in
candidate work, while the MySQL result shows that correlated-query setup and
execution remain a substantial performance tail.
