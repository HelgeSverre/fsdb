<!--
sha: f07f992
date: 2026-08-25T04:39:15Z
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
| Method          | Target | Mean       | Error     | StdDev  | Allocated |
|---------------- |------- |-----------:|----------:|--------:|----------:|
| **JoinUsersOrders** | **fsdb**   | **6,061.3 μs** | **176.32 μs** | **9.66 μs** |     **819 B** |
| **JoinUsersOrders** | **mysql**  |   **165.5 μs** |   **4.12 μs** | **0.23 μs** |     **808 B** |
