<!--
commit: ef4b4ab
targets: in-memory fsdb; durable MySQL 8.4
datasets: 10,000 and 100,000 articles
job: BenchmarkDotNet ShortRun, 3 warmups + 3 measured iterations
-->

## 10,000 articles

| Method | fsdb | MySQL 8.4 |
|---|---:|---:|
| Natural | 2.669 ms | 411.6 µs |
| Boolean | 3.191 ms | 1.127 ms |
| Accent-aware | 1.663 ms | 218.4 µs |
| Boolean prefix | 1.437 ms | 309.2 µs |

## 100,000 articles

| Method | fsdb | MySQL 8.4 |
|---|---:|---:|
| Natural | 54.718 ms | 3.831 ms |
| Boolean | 55.995 ms | 10.651 ms |
| Accent-aware | 29.305 ms | 1.800 ms |
| Boolean prefix | 33.890 ms | 2.617 ms |

The 100k ShortRun showed high variance on fsdb, so these values establish
order of magnitude and slope rather than a narrow regression threshold.
Compared with the preceding maintained-index run, sparse boolean evaluation
reduced the 10k case from 17.3 ms to 3.19 ms and the 100k case from 274 ms to
56.0 ms. Maintained prefix postings reduced the corresponding prefix cases
from 9.73 ms to 1.44 ms and from 159 ms to 33.9 ms.
