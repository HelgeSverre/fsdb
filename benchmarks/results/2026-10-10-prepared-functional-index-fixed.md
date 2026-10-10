<!--
sha: 37226943
date: 2026-10-10T04:50:10Z
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
| Method                                  | Target | Mean      | Error      | StdDev   | Gen0   | Allocated |
|---------------------------------------- |------- |----------:|-----------:|---------:|-------:|----------:|
| **PointSelectIdByPk**                       | **fsdb**   | **282.21 μs** |  **52.800 μs** | **2.894 μs** |      **-** |     **705 B** |
| FilterByComposedFunctionalIndex         | fsdb   | 362.71 μs | 161.739 μs | 8.865 μs |      - |     793 B |
| PreparedPointSelectIdByPk               | fsdb   | 242.22 μs |  58.406 μs | 3.201 μs |      - |     233 B |
| PreparedFilterByComposedFunctionalIndex | fsdb   | 277.54 μs |  61.924 μs | 3.394 μs |      - |     369 B |
| **PointSelectIdByPk**                       | **mysql**  |  **38.15 μs** |   **5.744 μs** | **0.315 μs** | **0.0610** |     **704 B** |
| FilterByComposedFunctionalIndex         | mysql  |  47.19 μs |   8.046 μs | 0.441 μs | 0.0610 |     792 B |
| PreparedPointSelectIdByPk               | mysql  |  32.19 μs |   5.852 μs | 0.321 μs |      - |     232 B |
| PreparedFilterByComposedFunctionalIndex | mysql  |  41.09 μs |   6.837 μs | 0.375 μs |      - |     368 B |
