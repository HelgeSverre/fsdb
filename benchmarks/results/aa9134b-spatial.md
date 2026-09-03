# Spatial bounds probe

ShortRun on Apple M2 Max with .NET 10.0.11 and MySQL 8.4.11. Both targets
used the same 10,000-point table and a three-point `MBRINTERSECTS` window.

| Method | Target | Mean | Allocated |
|---|---|---:|---:|
| FilterBySpatialBounds | fsdb | 215.24 us | 1.65 KB |
| FilterBySpatialBounds | mysql | 69.68 us | 1.65 KB |

The initial four-axis implementation measured 14.57 ms for fsdb on the same
workload. Replacing half-axis materialization with an augmented immutable
interval tree reduced the probe by roughly two orders of magnitude while
retaining persistent snapshot roots.
