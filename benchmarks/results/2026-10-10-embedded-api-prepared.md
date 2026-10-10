<!--
sha: 10144edb (working-tree implementation of Connection.Prepare)
date: 2026-10-10
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
target: in-memory embedded fsdb; one connection; 1,000 seeded rows
method: Release build; 50 warmups per case; median of five 500-call trials;
        alternating case order; no other benchmark or test workload
-->

| Case | Median µs/op | Median bytes/op |
|---|---:|---:|
| Literal text `Query` | 106.5 | 249,993 |
| Per-call `Execute` | 180.3 | 551,064 |
| Per-call `QueryValues` | 187.4 | 551,064 |
| Reused `PreparedCommand.Execute` | 102.0 | 319,299 |
| Reused `PreparedCommand.QueryValues` | 97.9 | 318,907 |

The prepared handle avoids parsing and validating the same SQL for each call.
For typed lookups in this run it took about 52% of the per-call time and
allocated about 58% as many bytes. Preparation and connection creation are
outside the timed loop. This is a directional same-process API measurement,
not a comparison with MySQL or a claim about server throughput.
