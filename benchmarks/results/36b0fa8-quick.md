<!--
sha: 36b0fa8
date: 2026-09-09T03:34:32Z
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
| Method                         | Target | Mean         | Error      | StdDev    | Gen0   | Allocated |
|------------------------------- |------- |-------------:|-----------:|----------:|-------:|----------:|
| **FilterByPrimaryKeyList**         | **fsdb**   |    **323.75 μs** | **166.117 μs** |  **9.105 μs** |      **-** |    **1694 B** |
| **FilterByPrimaryKeyList**         | **mysql**  |     **50.78 μs** |  **19.698 μs** |  **1.080 μs** | **0.1831** |    **1694 B** |
| **FilterByPrimaryKeyConstantList** | **fsdb**   | **17,762.25 μs** | **433.389 μs** | **23.756 μs** |      **-** |    **2666 B** |
| **FilterByPrimaryKeyConstantList** | **mysql**  |     **53.53 μs** |   **4.696 μs** |  **0.257 μs** | **0.3052** |    **2614 B** |
| **FilterBySecondaryRange**         | **fsdb**   |    **244.89 μs** | **121.184 μs** |  **6.642 μs** |      **-** |     **873 B** |
| **FilterBySecondaryRange**         | **mysql**  |     **43.80 μs** |   **8.026 μs** |  **0.440 μs** | **0.0610** |     **872 B** |
| **FilterByConstantSecondaryRange** | **fsdb**   |  **4,686.43 μs** | **443.467 μs** | **24.308 μs** |      **-** |     **955 B** |
| **FilterByConstantSecondaryRange** | **mysql**  |     **44.20 μs** |  **10.784 μs** |  **0.591 μs** | **0.0610** |     **944 B** |
