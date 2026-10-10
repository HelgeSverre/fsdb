<!--
sha: 54d47699
date: 2026-10-10T04:41:33Z
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
| Method                                  | Target | Mean         | Error         | StdDev       | Allocated |
|---------------------------------------- |------- |-------------:|--------------:|-------------:|----------:|
| **PreparedFilterByComposedFunctionalIndex** | **fsdb**   | **44,518.82 μs** | **95,532.275 μs** | **5,236.446 μs** |         **-** |
| **PreparedFilterByComposedFunctionalIndex** | **mysql**  |     **42.40 μs** |      **5.424 μs** |     **0.297 μs** |     **368 B** |
