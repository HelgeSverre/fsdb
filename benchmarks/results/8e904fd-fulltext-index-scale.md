<!--
commit: 8e904fd
targets: in-memory fsdb; durable MySQL 8.4
dataset: 100,000 articles
job: BenchmarkDotNet ShortRun, 3 warmups + 3 measured iterations
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
| Method                      | Target | Mean       | Error       | StdDev     | Allocated |
|---------------------------- |------- |-----------:|------------:|-----------:|----------:|
| **FullTextNaturalSearch**       | **fsdb**   |  **48.469 ms** |   **7.3400 ms** |  **0.4023 ms** |         **-** |
| FullTextBooleanSearch       | fsdb   | 274.388 ms | 462.4215 ms | 25.3469 ms |         - |
| FullTextAccentSearch        | fsdb   |  28.367 ms |  25.9798 ms |  1.4240 ms |     546 B |
| FullTextBooleanPrefixSearch | fsdb   | 159.193 ms | 280.2304 ms | 15.3604 ms |         - |
| **FullTextNaturalSearch**       | **mysql**  |   **3.719 ms** |   **0.9936 ms** |  **0.0545 ms** |     **508 B** |
| FullTextBooleanSearch       | mysql  |  10.813 ms |   4.6793 ms |  0.2565 ms |     521 B |
| FullTextAccentSearch        | mysql  |   1.822 ms |   0.0362 ms |  0.0020 ms |     506 B |
| FullTextBooleanPrefixSearch | mysql  |   2.639 ms |   0.3470 ms |  0.0190 ms |     508 B |
