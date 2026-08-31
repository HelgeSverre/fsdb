<!--
sha: 2d00ef1
date: 2026-08-31T10:56:16Z
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
| Method                 | Target | Mean      | Error      | StdDev   | Gen0   | Allocated |
|----------------------- |------- |----------:|-----------:|---------:|-------:|----------:|
| **FilterByPrimaryKeyList** | **fsdb**   | **277.11 μs** | **139.123 μs** | **7.626 μs** |      **-** |   **1.65 KB** |
| **FilterByPrimaryKeyList** | **mysql**  |  **51.15 μs** |   **4.253 μs** | **0.233 μs** | **0.1831** |   **1.65 KB** |
