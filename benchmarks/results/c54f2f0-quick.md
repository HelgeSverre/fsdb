<!--
sha: c54f2f0
date: 2026-09-09T04:06:32Z
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
| Method                           | Target | Mean        | Error        | StdDev    | Gen0   | Allocated |
|--------------------------------- |------- |------------:|-------------:|----------:|-------:|----------:|
| **FilterBySecondaryBetween**         | **fsdb**   | **3,140.76 μs** | **1,710.222 μs** | **93.743 μs** |      **-** |     **869 B** |
| **FilterBySecondaryBetween**         | **mysql**  |    **42.49 μs** |     **1.724 μs** |  **0.094 μs** | **0.0610** |     **863 B** |
| **FilterByConstantSecondaryBetween** | **fsdb**   | **4,642.82 μs** |   **441.915 μs** | **24.223 μs** |      **-** |     **946 B** |
| **FilterByConstantSecondaryBetween** | **mysql**  |    **42.96 μs** |     **1.696 μs** |  **0.093 μs** | **0.0610** |     **935 B** |
