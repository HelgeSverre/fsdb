# Spatial reference lookup after UTM catalog expansion

The UTM work grew fsdb's built-in spatial reference catalog from 7 entries at
`e96d869f` to 160 entries at `db9bf3f0`. This native, same-host microbenchmark
checks whether the existing linear `SpatialReferenceSystems.tryFind` lookup
became expensive enough to warrant a more complex index. It measures the
compiled lookup directly, not SQL execution or wire latency.

Both revisions were built in Debug with .NET 10 on the same Apple Silicon host.
The older revision was extracted with `git archive`; the current revision used
the working checkout. The [script](../scripts/srs-lookup.fsx) obtains a compiled
delegate to the internal lookup through reflection, warms each SRID 20,000
times, then runs five trials of one million calls. Values below are median
nanoseconds per call; the baseline lacks SRID 26923, so that row is a miss
there. No MySQL process is involved in this isolated engine measurement.

| SRID | Baseline ns | Current ns | Meaning |
|---|---:|---:|---|
| 0 | 32.8 | 45.8 | first catalog entry; noisy across trials |
| 4326 | 20.1 | 18.9 | common geographic hit |
| 3395 | 28.3 | 26.0 | common projected hit |
| 26923 | 23.5 | 249.0 | baseline miss; current late-list hit |
| 999999 | 22.8 | 245.6 | full-list miss |

The five-trial ranges for the current revision were 24.4–46.6 ns (SRID 0),
18.2–19.1 ns (4326), 25.4–26.5 ns (3395), 248.2–251.4 ns (26923), and
244.0–248.4 ns (missing). The corresponding baseline ranges were
18.3–48.2, 19.7–20.6, 27.0–28.6, 22.5–24.6, and 22.3–23.5 ns. The SRID 0
variation shows that these tiny timings are sensitive to runtime effects;
the late-list and miss differences are much larger than that noise.

## Results

The 160-entry linear scan costs about 0.25 µs at the end of the list, roughly
0.22 µs more than the old miss path. Common early hits are unchanged within
measurement noise. The catalog lookup is invoked at spatial-function setup,
not once per geometry vertex. This snapshot does not show a SQL-level
bottleneck, so no lookup-index change is justified by the current evidence.
If a future profile shows heavy unknown-SRID or late-SRID traffic, the shared
catalog can be indexed without changing its public behavior.
