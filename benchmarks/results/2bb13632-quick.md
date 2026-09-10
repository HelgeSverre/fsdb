<!--
sha: 2bb13632
date: 2026-09-10T23:35:24Z
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
| Method                           | Target | Mean           | Error          | StdDev       | Allocated |
|--------------------------------- |------- |---------------:|---------------:|-------------:|----------:|
| **CorrelatedFunctionalEquality** | **fsdb**   |     **2,223.2 µs** |       **107.5 µs** |      **5.89 µs** |     **797 B** |
| CorrelatedFunctionalScan         | fsdb   | 5,301,850.2 µs | 1,145,535.5 µs | 62,790.67 µs |         - |
| **CorrelatedFunctionalEquality** | **mysql**  |       **420.8 µs** |       **115.8 µs** |      **6.35 µs** |     **793 B** |
| CorrelatedFunctionalScan         | mysql  |   283,258.2 µs |    88,233.4 µs |  4,836.37 µs |         - |
