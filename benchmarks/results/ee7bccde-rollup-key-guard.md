# Empty ROLLUP membership allocation guard

Baseline `ee7bccde`; candidate is the same revision with an empty-list guard
before `List.contains expr ctx.RollupKeys` in `evalExpr`. The comparison is
unnecessary when no subtotal keys are active. Nonempty lists use the same
structural comparison as before.

The [focused probe](../scripts/rollup-key-membership.fsx) recorded 8,007,264
allocated bytes for 100,000 empty-list comparisons versus 232 bytes with the
guard. These totals include measurement overhead; they identify approximately
80 avoidable bytes per expression evaluation on this runtime.

The [grouped workload](../scripts/grouped-inputs.fsx) checks every result over
10,000 rows and 100 interleaved groups. Embedded in-memory Debug engine, Darwin
arm64, .NET SDK 10.0.401, eight logical processors, 4 GiB GC heap cap. Five
warmups and nine alternating batches of three queries. Baseline ran first;
after the edit and full gate, the candidate ran. No concurrent fsdb build,
test, oracle, or profile ran during measurements. Background apps remained active.

| Query | Before ms | After ms | Before bytes | After bytes | Allocation reduction |
|---|---:|---:|---:|---:|---:|
| scalar-sum | 6.567 | 5.731 | 7718704 | 6918544 | 10.37% |
| grouped-sum | 13.908 | 12.212 | 15190776 | 13574536 | 10.64% |
| grouped-rollup | 23.310 | 21.203 | 28276768 | 25860528 | 8.54% |
| grouped-distinct | 20.224 | 18.317 | 24435864 | 22011544 | 9.92% |
| grouped-count | 8.154 | 7.193 | 9627568 | 8811328 | 8.48% |
| grouped-multiple | 33.736 | 28.297 | 37380496 | 33340000 | 10.81% |

Allocations fell 8.5–10.8%, approximately 0.8–4.0 MB per query. This explains
most of the increase identified in the [prior checkpoint](0382c3e8-encoded-text-worktree.md).
It does not remove the cost of retaining additional execution metadata.
Timing medians fell in this pair, but sequential runs and noisy samples do not
establish a stable latency improvement of that magnitude. No MySQL comparison
or durable-write claim is made.

Validation: `just check`, 3,067 tests passed with zero build warnings or errors.
Existing tests cover ROLLUP subtotal values, metadata, and volatile evaluation.

## Before

```text
scalar-sum input=10000 median-ms=6.567 median-bytes=7718704 samples-ms=[8.0929; 6.450433333; 6.487566667; 6.628966667; 6.877733333; 6.693966667;
 6.481166667; 6.425966667; 6.5675]
grouped-sum input=10000 median-ms=13.908 median-bytes=15190776 samples-ms=[17.71466667; 14.42323333; 14.19206667; 13.90806667; 13.70633333; 13.65976667;
 13.64216667; 13.68113333; 13.92893333]
grouped-rollup input=10000 median-ms=23.310 median-bytes=28276768 samples-ms=[45.4518; 24.08396667; 24.118; 23.8757; 22.9256; 23.00096667; 23.04806667;
 23.3097; 23.19443333]
grouped-distinct input=10000 median-ms=20.224 median-bytes=24435864 samples-ms=[22.03486667; 21.0265; 20.77096667; 19.9892; 20.22356667; 19.5916; 19.71083333;
 19.9078; 21.3762]
grouped-count input=10000 median-ms=8.154 median-bytes=9627568 samples-ms=[8.5968; 8.208733333; 8.553566667; 8.145233333; 7.7391; 8.154466667; 8.705266667;
 7.883033333; 7.5995]
grouped-multiple input=10000 median-ms=33.736 median-bytes=37380496 samples-ms=[34.2043; 33.73573333; 37.99903333; 35.42006667; 32.3965; 41.07676667; 31.3619;
 31.931; 30.43956667]
```

## After

```text
scalar-sum input=10000 median-ms=5.731 median-bytes=6918544 samples-ms=[8.182366667; 6.042; 6.075266667; 5.633333333; 5.615233333; 5.8199; 5.731033333;
 5.5499; 5.592266667]
grouped-sum input=10000 median-ms=12.212 median-bytes=13574536 samples-ms=[17.7259; 14.37306667; 13.0612; 12.2118; 11.81116667; 12.30383333; 11.94966667;
 11.89416667; 11.74756667]
grouped-rollup input=10000 median-ms=21.203 median-bytes=25860528 samples-ms=[29.19633333; 23.4153; 21.20326667; 21.60743333; 19.23296667; 29.08973333;
 20.03773333; 19.762; 19.24093333]
grouped-distinct input=10000 median-ms=18.317 median-bytes=22011544 samples-ms=[45.0982; 19.58546667; 18.6739; 19.07626667; 16.747; 16.66256667; 17.31883333;
 18.3165; 18.1033]
grouped-count input=10000 median-ms=7.193 median-bytes=8811328 samples-ms=[7.720733333; 7.801666667; 7.689833333; 7.5408; 6.971633333; 6.931666667; 7.0294;
 7.1932; 7.0335]
grouped-multiple input=10000 median-ms=28.297 median-bytes=33340000 samples-ms=[31.459; 31.6391; 29.53963333; 29.2453; 27.7276; 27.81123333; 28.08716667;
 28.297; 27.9072]
```
