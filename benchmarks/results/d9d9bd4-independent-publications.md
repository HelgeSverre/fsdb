<!--
sha: d9d9bd4
date: 2026-09-07T22:06:41Z
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
| Method                | Target | Mean       | Error      | StdDev   | Gen0   | Allocated |
|---------------------- |------- |-----------:|-----------:|---------:|-------:|----------:|
| **ConcurrentInsertBurst** | **fsdb**   | **3,790.0 μs** | **1,519.4 μs** | **83.29 μs** | **3.9063** |  **46.52 KB** |
| **ConcurrentInsertBurst** | **mysql**  |   **894.2 μs** | **1,626.3 μs** | **89.14 μs** | **4.8828** |  **46.89 KB** |

The same ShortRun at parent `44eac9b` measured fsdb at 4,275.5 μs and MySQL
at 935.3 μs. Shared schema access during ordinary commit reduced fsdb's
concurrent insert burst by 11.4%; managed allocation remained 46.52 KB.
