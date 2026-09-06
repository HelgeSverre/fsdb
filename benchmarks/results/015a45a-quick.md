<!--
sha: 015a45a
date: 2026-09-06T01:19:29Z
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
| Method                          | Target | Mean         | Error          | StdDev        | Gen0   | Allocated |
|-------------------------------- |------- |-------------:|---------------:|--------------:|-------:|----------:|
| **FullTextJoinedPointIntersection** | **fsdb**   |    **332.18 μs** |     **202.043 μs** |     **11.075 μs** |      **-** |     **521 B** |
| **FullTextJoinedPointIntersection** | **mysql**  |  **1,982.87 μs** |      **86.309 μs** |      **4.731 μs** |      **-** |     **524 B** |
| **PointSelectByPk**                 | **fsdb**   |    **256.21 μs** |     **173.044 μs** |      **9.485 μs** |      **-** |     **881 B** |
| UpdateByNonIndexed              | fsdb   |  6,182.10 μs |   1,630.682 μs |     89.383 μs |      - |     731 B |
| **PointSelectByPk**                 | **mysql**  |     **40.16 μs** |       **4.592 μs** |      **0.252 μs** | **0.0610** |     **880 B** |
| UpdateByNonIndexed              | mysql  |  3,764.47 μs |     849.051 μs |     46.539 μs |      - |     788 B |
| **CountSelectiveIndexedRange**      | **fsdb**   |    **213.09 μs** |     **104.552 μs** |      **5.731 μs** |      **-** |     **857 B** |
| CountSelectiveScan              | fsdb   |  4,351.07 μs |   1,891.239 μs |    103.665 μs |      - |     883 B |
| CountUnselectiveIndexedRange    | fsdb   |  2,082.63 μs |     914.949 μs |     50.151 μs |      - |     488 B |
| CountUnselectiveScan            | fsdb   |  2,672.07 μs |     824.443 μs |     45.190 μs |      - |     493 B |
| CountSelectiveIndexedEquality   | fsdb   |    264.41 μs |     109.412 μs |      5.997 μs |      - |     488 B |
| CountSelectiveEqualityScan      | fsdb   |  2,375.22 μs |      94.441 μs |      5.177 μs |      - |     492 B |
| LowCardinalityIndexedJoin       | fsdb   |  2,926.24 μs |     418.272 μs |     22.927 μs |      - |     496 B |
| LowCardinalityHashJoin          | fsdb   |  3,170.36 μs |     126.097 μs |      6.912 μs |      - |     493 B |
| LowCardinalityJoinedFilter      | fsdb   |  5,495.42 μs |   1,953.570 μs |    107.082 μs |      - |     499 B |
| DecimalInSubquery               | fsdb   | 22,080.26 μs | 147,460.376 μs |  8,082.801 μs |      - |     510 B |
| CompositeInSubquery             | fsdb   |  1,310.70 μs |      60.036 μs |      3.291 μs |      - |     490 B |
| QuantifiedMembership            | fsdb   |  2,078.42 μs |     285.874 μs |     15.670 μs |      - |     493 B |
| CorrelatedOrderCount            | fsdb   |  1,229.98 μs |     446.110 μs |     24.453 μs |      - |     507 B |
| **CountSelectiveIndexedRange**      | **mysql**  |     **43.14 μs** |       **7.592 μs** |      **0.416 μs** | **0.0610** |     **856 B** |
| CountSelectiveScan              | mysql  |  1,061.32 μs |      57.189 μs |      3.135 μs |      - |     874 B |
| CountUnselectiveIndexedRange    | mysql  |  1,017.76 μs |     197.829 μs |     10.844 μs |      - |     490 B |
| CountUnselectiveScan            | mysql  |    993.02 μs |      22.633 μs |      1.241 μs |      - |     490 B |
| CountSelectiveIndexedEquality   | mysql  |     58.05 μs |       9.947 μs |      0.545 μs |      - |     488 B |
| CountSelectiveEqualityScan      | mysql  |  1,109.96 μs |     125.640 μs |      6.887 μs |      - |     570 B |
| LowCardinalityIndexedJoin       | mysql  |  1,644.75 μs |     411.843 μs |     22.575 μs |      - |     490 B |
| LowCardinalityHashJoin          | mysql  |  1,774.73 μs |     303.259 μs |     16.623 μs |      - |     570 B |
| LowCardinalityJoinedFilter      | mysql  |  1,798.56 μs |     294.279 μs |     16.130 μs |      - |     570 B |
| DecimalInSubquery               | mysql  |  8,216.18 μs |     641.870 μs |     35.183 μs |      - |     585 B |
| CompositeInSubquery             | mysql  |  7,320.04 μs |   1,026.959 μs |     56.291 μs |      - |     496 B |
| QuantifiedMembership            | mysql  |  7,465.08 μs |  12,625.696 μs |    692.057 μs |      - |     591 B |
| CorrelatedOrderCount            | mysql  |    210.82 μs |      41.299 μs |      2.264 μs |      - |     504 B |
| **CountHalfIndexedLiteralIn**       | **fsdb**   | **27,459.85 μs** |  **23,870.487 μs** |  **1,308.422 μs** |      **-** |     **818 B** |
| CountHalfLiteralInScan          | fsdb   | 57,045.25 μs | 399,552.627 μs | 21,900.828 μs |      - |     889 B |
| **CountHalfIndexedLiteralIn**       | **mysql**  |    **805.25 μs** |     **517.634 μs** |     **28.373 μs** |      **-** |     **777 B** |
| CountHalfLiteralInScan          | mysql  |  1,279.11 μs |     111.258 μs |      6.098 μs |      - |     866 B |
