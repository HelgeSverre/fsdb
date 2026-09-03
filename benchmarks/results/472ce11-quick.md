<!--
sha: 472ce11
date: 2026-09-03T02:07:42Z
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
| Method                        | Target | Mean      | Error     | StdDev   | Gen0   | Allocated |
|------------------------------ |------- |----------:|----------:|---------:|-------:|----------:|
| **SelectCompressiblePayload**     | **fsdb**   | **153.02 μs** |  **9.916 μs** | **0.544 μs** |      **-** |     **488 B** |
| SelectCompressiblePayloadZlib | fsdb   | 197.62 μs | 49.780 μs | 2.729 μs | 0.7324 |    7752 B |
| **SelectCompressiblePayload**     | **mysql**  |  **41.68 μs** |  **2.398 μs** | **0.131 μs** |      **-** |     **488 B** |
| SelectCompressiblePayloadZlib | mysql  |  58.75 μs |  5.473 μs | 0.300 μs | 0.9155 |    7832 B |
