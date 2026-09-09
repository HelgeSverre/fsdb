<!--
sha: e1c2bbe
date: 2026-09-09T03:50:58Z
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
| Method                         | Target | Mean      | Error      | StdDev    | Gen0   | Allocated |
|------------------------------- |------- |----------:|-----------:|----------:|-------:|----------:|
| **FilterByPrimaryKeyList**         | **fsdb**   | **321.83 μs** | **127.375 μs** |  **6.982 μs** |      **-** |    **1694 B** |
| **FilterByPrimaryKeyList**         | **mysql**  |  **49.92 μs** |   **7.118 μs** |  **0.390 μs** | **0.1831** |    **1694 B** |
| **FilterByPrimaryKeyConstantList** | **fsdb**   | **353.25 μs** | **174.989 μs** |  **9.592 μs** |      **-** |    **2614 B** |
| **FilterByPrimaryKeyConstantList** | **mysql**  |  **52.96 μs** |  **14.032 μs** |  **0.769 μs** | **0.3052** |    **2614 B** |
| **FilterBySecondaryRange**         | **fsdb**   | **248.72 μs** |  **89.536 μs** |  **4.908 μs** |      **-** |     **873 B** |
| **FilterBySecondaryRange**         | **mysql**  |  **43.42 μs** |  **12.048 μs** |  **0.660 μs** | **0.0610** |     **872 B** |
| **FilterByConstantSecondaryRange** | **fsdb**   | **277.00 μs** | **483.053 μs** | **26.478 μs** |      **-** |     **945 B** |
| **FilterByConstantSecondaryRange** | **mysql**  |  **44.29 μs** |  **10.758 μs** |  **0.590 μs** | **0.0610** |     **944 B** |
