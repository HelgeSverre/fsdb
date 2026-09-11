<!--
sha: 585c70c3
date: 2026-09-11T03:18:54Z
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
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-WCTQBO : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3

```
| Method                                       | Target | Mean         | Error         | StdDev       | Gen0   | Allocated |
|--------------------------------------------- |------- |-------------:|--------------:|-------------:|-------:|----------:|
| **CountSelectiveIndexedRange**                   | **fsdb**   |    **287.82 μs** |     **26.677 μs** |     **9.513 μs** |      **-** |     **857 B** |
| CountSelectiveScan                           | fsdb   |  4,918.36 μs |    222.873 μs |    79.479 μs |      - |     882 B |
| **CountSelectiveIndexedRange**                   | **mysql**  |     **46.05 μs** |      **4.591 μs** |     **1.637 μs** | **0.0610** |     **856 B** |
| CountSelectiveScan                           | mysql  |  1,087.65 μs |      6.739 μs |     1.750 μs |      - |     874 B |
| **FilterByComposedFunctionalRange**              | **fsdb**   |    **407.63 μs** |     **45.225 μs** |    **16.128 μs** |      **-** |     **992 B** |
| FilterByComposedFunctionalRangeScan          | fsdb   | 60,064.12 μs | 12,031.186 μs | 4,290.437 μs |      - |    1189 B |
| FilterByComposedFunctionalRangeOrderById     | fsdb   |    430.99 μs |     47.321 μs |    16.875 μs |      - |    1016 B |
| FilterByComposedFunctionalRangeOrderByIdScan | fsdb   | 57,460.26 μs | 12,666.489 μs | 4,516.992 μs |      - |    1213 B |
| **FilterByComposedFunctionalRange**              | **mysql**  |     **55.97 μs** |      **0.716 μs** |     **0.255 μs** | **0.0610** |     **991 B** |
| FilterByComposedFunctionalRangeScan          | mysql  |  2,829.84 μs |     46.397 μs |    16.546 μs |      - |    1123 B |
| FilterByComposedFunctionalRangeOrderById     | mysql  |     57.92 μs |      1.404 μs |     0.501 μs | 0.0610 |    1015 B |
| FilterByComposedFunctionalRangeOrderByIdScan | mysql  |  2,834.67 μs |     24.425 μs |     8.710 μs |      - |    1067 B |
| **OrderBySecondaryRange**                        | **fsdb**   |    **342.77 μs** |     **36.650 μs** |    **13.070 μs** |      **-** |    **1001 B** |
| **OrderBySecondaryRange**                        | **mysql**  |     **52.74 μs** |      **0.521 μs** |     **0.186 μs** | **0.0610** |    **1000 B** |
| **FilterBySecondaryRange**                       | **fsdb**   |    **293.73 μs** |     **26.876 μs** |     **9.584 μs** |      **-** |     **873 B** |
| FilterBySecondaryBetween                     | fsdb   |    263.40 μs |     31.975 μs |    11.403 μs |      - |     864 B |
| UpdateBySecondaryRange                       | fsdb   |    427.78 μs |     34.145 μs |    12.177 μs |      - |     848 B |
| **FilterBySecondaryRange**                       | **mysql**  |     **46.14 μs** |      **0.483 μs** |     **0.172 μs** | **0.0610** |     **872 B** |
| FilterBySecondaryBetween                     | mysql  |     44.58 μs |      0.356 μs |     0.127 μs | 0.0610 |     863 B |
| UpdateBySecondaryRange                       | mysql  |    133.36 μs |     17.103 μs |     6.099 μs |      - |     911 B |
