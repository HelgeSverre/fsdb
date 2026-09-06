<!--
sha: 5bb6fe3
date: 2026-09-06T01:03:21Z
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
| Method                          | Target | Mean         | Error         | StdDev       | Gen0   | Allocated |
|-------------------------------- |------- |-------------:|--------------:|-------------:|-------:|----------:|
| **FullTextJoinedPointIntersection** | **fsdb**   |    **300.50 μs** |     **76.428 μs** |     **4.189 μs** |      **-** |     **521 B** |
| **FullTextJoinedPointIntersection** | **mysql**  |  **2,149.84 μs** |    **743.929 μs** |    **40.777 μs** |      **-** |     **524 B** |
| **PointSelectByPk**                 | **fsdb**   |    **252.51 μs** |    **136.816 μs** |     **7.499 μs** |      **-** |     **881 B** |
| UpdateByNonIndexed              | fsdb   |  6,305.52 μs |  1,810.163 μs |    99.221 μs |      - |     728 B |
| **PointSelectByPk**                 | **mysql**  |     **38.85 μs** |     **11.080 μs** |     **0.607 μs** | **0.0610** |     **880 B** |
| UpdateByNonIndexed              | mysql  |  7,067.66 μs |  6,722.479 μs |   368.482 μs |      - |     790 B |
| **CountSelectiveIndexedRange**      | **fsdb**   |  **1,407.90 μs** |  **3,215.269 μs** |   **176.240 μs** |      **-** |     **859 B** |
| CountSelectiveScan              | fsdb   |  9,110.35 μs | 14,556.312 μs |   797.881 μs |      - |     873 B |
| CountUnselectiveIndexedRange    | fsdb   |  2,703.93 μs |  8,130.162 μs |   445.642 μs |      - |     493 B |
| CountUnselectiveScan            | fsdb   |  2,867.80 μs |    433.821 μs |    23.779 μs |      - |     493 B |
| CountSelectiveIndexedEquality   | fsdb   |    274.29 μs |     77.669 μs |     4.257 μs |      - |     489 B |
| CountSelectiveEqualityScan      | fsdb   |  2,318.24 μs |    154.945 μs |     8.493 μs |      - |     493 B |
| LowCardinalityIndexedJoin       | fsdb   |  3,623.08 μs |  2,021.034 μs |   110.780 μs |      - |     493 B |
| LowCardinalityHashJoin          | fsdb   |  3,638.55 μs |    761.055 μs |    41.716 μs |      - |     492 B |
| LowCardinalityJoinedFilter      | fsdb   |  6,670.32 μs |  3,996.437 μs |   219.058 μs |      - |     499 B |
| DecimalInSubquery               | fsdb   | 11,489.06 μs |  2,303.197 μs |   126.246 μs |      - |     509 B |
| CompositeInSubquery             | fsdb   |  1,511.95 μs |  3,471.360 μs |   190.277 μs |      - |     491 B |
| QuantifiedMembership            | fsdb   |  2,199.64 μs |  1,403.894 μs |    76.952 μs |      - |     493 B |
| CorrelatedOrderCount            | fsdb   |  1,257.72 μs |    414.578 μs |    22.724 μs |      - |     507 B |
| **CountSelectiveIndexedRange**      | **mysql**  |     **43.93 μs** |      **4.593 μs** |     **0.252 μs** | **0.0610** |     **856 B** |
| CountSelectiveScan              | mysql  |  1,070.89 μs |     43.349 μs |     2.376 μs |      - |     874 B |
| CountUnselectiveIndexedRange    | mysql  |  1,017.62 μs |    228.882 μs |    12.546 μs |      - |     490 B |
| CountUnselectiveScan            | mysql  |    994.72 μs |     85.045 μs |     4.662 μs |      - |     490 B |
| CountSelectiveIndexedEquality   | mysql  |     58.34 μs |     17.357 μs |     0.951 μs |      - |     488 B |
| CountSelectiveEqualityScan      | mysql  |  1,111.47 μs |    153.009 μs |     8.387 μs |      - |     570 B |
| LowCardinalityIndexedJoin       | mysql  |  1,680.02 μs |    348.403 μs |    19.097 μs |      - |     490 B |
| LowCardinalityHashJoin          | mysql  |  2,055.57 μs |  7,652.703 μs |   419.470 μs |      - |     570 B |
| LowCardinalityJoinedFilter      | mysql  |  2,364.33 μs |    157.406 μs |     8.628 μs |      - |     572 B |
| DecimalInSubquery               | mysql  | 10,210.95 μs |  2,810.984 μs |   154.080 μs |      - |     585 B |
| CompositeInSubquery             | mysql  |  9,170.79 μs |  3,414.495 μs |   187.160 μs |      - |     505 B |
| QuantifiedMembership            | mysql  |  7,964.13 μs |  5,207.678 μs |   285.450 μs |      - |     585 B |
| CorrelatedOrderCount            | mysql  |    327.85 μs |    324.304 μs |    17.776 μs |      - |     505 B |
| **CountHalfIndexedLiteralIn**       | **fsdb**   | **29,016.30 μs** |  **7,414.637 μs** |   **406.421 μs** |      **-** |     **818 B** |
| CountHalfLiteralInScan          | fsdb   | 48,496.97 μs | 35,230.097 μs | 1,931.080 μs |      - |     908 B |
| **CountHalfIndexedLiteralIn**       | **mysql**  |  **1,074.37 μs** |    **225.127 μs** |    **12.340 μs** |      **-** |     **777 B** |
| CountHalfLiteralInScan          | mysql  |  1,759.61 μs |    361.135 μs |    19.795 μs |      - |     866 B |
