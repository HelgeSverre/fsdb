<!--
sha: 0cc8aee
date: 2026-09-09T07:39:15Z
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
| Method                            | Target | Mean      | Error      | StdDev    | Gen0   | Allocated |
|---------------------------------- |------- |----------:|-----------:|----------:|-------:|----------:|
| **FilterBySecondaryEquality**         | **fsdb**   | **461.82 μs** | **136.516 μs** |  **7.483 μs** |      **-** |     **520 B** |
| **FilterBySecondaryEquality**         | **mysql**  | **180.48 μs** |  **61.175 μs** |  **3.353 μs** |      **-** |     **520 B** |
| **FilterByCompetingSecondaryIndexes** | **fsdb**   | **335.16 μs** | **138.599 μs** |  **7.597 μs** |      **-** |     **747 B** |
| **FilterByCompetingSecondaryIndexes** | **mysql**  |  **46.24 μs** |  **10.983 μs** |  **0.602 μs** | **0.0610** |     **747 B** |
| **FilterBySecondaryRange**            | **fsdb**   | **263.43 μs** | **198.198 μs** | **10.864 μs** |      **-** |     **873 B** |
| **FilterBySecondaryRange**            | **mysql**  |  **44.39 μs** |   **6.288 μs** |  **0.345 μs** | **0.0610** |     **872 B** |
