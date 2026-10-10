<!--
sha: 9754c9b7
date: 2026-10-10T04:25:45Z
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
| Method                          | Target | Mean      | Error    | StdDev   | Gen0   | Allocated |
|-------------------------------- |------- |----------:|---------:|---------:|-------:|----------:|
| **PointSelectIdByPk**               | **fsdb**   | **292.35 μs** | **43.59 μs** | **2.389 μs** |      **-** |     **705 B** |
| FilterByComposedFunctionalIndex | fsdb   | 385.49 μs | 35.93 μs | 1.969 μs |      - |     793 B |
| **PointSelectIdByPk**               | **mysql**  |  **38.67 μs** | **13.48 μs** | **0.739 μs** | **0.0610** |     **704 B** |
| FilterByComposedFunctionalIndex | mysql  |  49.37 μs | 29.85 μs | 1.636 μs | 0.0610 |     792 B |
