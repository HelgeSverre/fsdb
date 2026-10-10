<!--
sha: 36185ad2
date: 2026-10-10T07:07:52Z
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
| Method                 | Target | Mean      | Error     | StdDev    | Gen0   | Allocated |
|----------------------- |------- |----------:|----------:|----------:|-------:|----------:|
| **PointSelectByPk**        | **fsdb**   | **436.58 μs** | **231.56 μs** | **12.693 μs** |      **-** |     **881 B** |
| UpdateSingleRow        | fsdb   | 472.25 μs | 238.55 μs | 13.076 μs |      - |     680 B |
| **PointSelectByPk**        | **mysql**  |  **44.14 μs** |  **14.93 μs** |  **0.818 μs** | **0.0610** |     **880 B** |
| UpdateSingleRow        | mysql  | 131.53 μs |  80.23 μs |  4.398 μs |      - |     743 B |
| **UpdateBySecondaryRange** | **fsdb**   | **650.25 μs** | **142.65 μs** |  **7.819 μs** |      **-** |     **848 B** |
| **UpdateBySecondaryRange** | **mysql**  | **127.17 μs** |  **92.52 μs** |  **5.071 μs** |      **-** |     **911 B** |
