# Boolean full-text proximity windows

Boolean proximity matching now deduplicates query keys and finds the smallest
document window with a linear sliding scan. MySQL's distance boundary is
strict: words at positions `lo` and `hi` match `@N` when `hi - lo < N`.
Repeated query words share one proximity requirement, including the single-key
`@0` case.

Phrase and proximity matches now retain the normal TF × IDF² contribution of
each distinct indexed word. The old path assigned a synthetic phrase document
frequency and discarded the words' stored frequencies, producing different
scores and rankings from MySQL.

Both measurements used the default deterministic dataset (10,000 articles),
MySQL 8.4.11, and BenchmarkDotNet `ShortRun` on the same machine. The stress
query repeats a word twelve times before two distinct terms and uses an
impossible `@1` window. This preserves the same empty result on both engines
while exposing the old Cartesian occurrence search.

| Build | Target | Mean | Median |
|---|---|---:|---:|
| `9cceb5e` | fsdb | 125.684 ms | 119.935 ms |
| `9cceb5e` | MySQL | 19.772 ms | 19.884 ms |
| `bb24818` | fsdb | 8.319 ms | 8.209 ms |
| `bb24818` | MySQL | 21.524 ms | 21.500 ms |

The fsdb mean improved by about 15.1× in these short runs. The post-change
mean was about 2.6× faster than the matched MySQL mean for this deliberately
adversarial query. ShortRun uses three measured iterations, so these values
establish direction and approximate scale rather than production guarantees.
