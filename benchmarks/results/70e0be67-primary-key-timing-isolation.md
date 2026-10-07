# Primary-key timing test isolation

This is a test-harness comparison, not an fsdb-versus-MySQL benchmark or an
engine speedup claim. Runs use the same macOS host, .NET SDK 10.0.401,
`DOTNET_PROCESSOR_COUNT=8`, a 4 GiB `DOTNET_GCHeapHardLimit`, and the tests'
in-memory stores. MSBuild node reuse and the CLI build server are disabled.

The committed control is `70e0be671e71f61b5d53e5f821b8be67710c2f1a`, built from
a clean Git archive. The working-tree runs add view creation-context metadata
and its export/recovery regression. Neither change alters primary-key access.

| Run | Observation |
|---|---|
| Working tree, full suite | 10k median 0.117700 ms, 40k median 0.327400 ms; ratio 2.781648 fails the 2.5 limit |
| Working tree, isolated ratio test | Passes the unchanged 2.5 limit |
| Working tree, full-suite repeat | 10k median 0.117700 ms, 40k median 0.384900 ms; ratio 3.270178 fails |
| Clean committed control, full suite | Separate 50k-row point lookup takes 27.9101 ms and fails its 20 ms limit; ratio test passes |
| Working tree, both timing cases sequenced | Full suite passes all 3,031 tests; both original timing limits remain enabled |

Concurrent fixture construction shares process-wide CPU and GC resources with
the timing probes. `TestSupport.processGlobalCase` schedules the two probes in
Expecto's sequential phase. Fixture sizes, query shapes, warmup, median sampling,
and assertion thresholds are unchanged. Test names and diagnostics no longer
claim that a short timing ratio proves asymptotic complexity.

The observations support isolating the tests; they do not establish an engine
latency change. Passing runs do not print their measured medians, so no
post-isolation absolute latency is inferred.

Local logs:

- `/tmp/fsdb-view-export-check.log`
- `/tmp/fsdb-view-export-pk-control.log`
- `/tmp/fsdb-view-export-check-repeat.log`
- `/tmp/fsdb-view-export-baseline-check.log`
- `/tmp/fsdb-view-export-sequenced-check.log`
