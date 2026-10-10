<!--
sha: 36185ad2
date: 2026-10-10T07:09:33Z
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
| Method                 | Target | Mean      | Error      | StdDev    | Gen0   | Allocated |
|----------------------- |------- |----------:|-----------:|----------:|-------:|----------:|
| **PointSelectByPk**        | **fsdb**   | **460.97 μs** | **325.278 μs** | **17.830 μs** |      **-** |     **881 B** |
| UpdateSingleRow        | fsdb   | 500.55 μs | 158.417 μs |  8.683 μs |      - |     680 B |
| **PointSelectByPk**        | **mysql**  |  **44.55 μs** |   **9.278 μs** |  **0.509 μs** | **0.0610** |     **880 B** |
| UpdateSingleRow        | mysql  | 135.21 μs |  91.674 μs |  5.025 μs |      - |     743 B |
| **UpdateBySecondaryRange** | **fsdb**   | **667.87 μs** | **233.993 μs** | **12.826 μs** |      **-** |     **848 B** |
| **UpdateBySecondaryRange** | **mysql**  | **136.60 μs** |  **60.038 μs** |  **3.291 μs** |      **-** |     **911 B** |
