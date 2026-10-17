# General-collation synthetic-weight folding

The general-collation key path previously walked each input separately for
ten synthetic-weight families. This change retains the same marker and weight
mapping in one dispatch and one escape-aware walk.

Release builds of `2b31b1ff` and the candidate ran sequentially on the same
macOS host. [`general-ci-key-profile.fsx`](../scripts/general-ci-key-profile.fsx)
generates 240,000 keys from ASCII, Han, kana, enclosed symbols, combining
marks, and format controls. An alternating baseline/candidate/candidate/
baseline/baseline/candidate run produced:

| Version | Elapsed (ms) | Allocated (bytes) |
|---|---:|---:|
| Baseline | 131, 134, 130 | 120,960,040 each |
| Candidate | 104, 104, 105 | 114,480,040 each |

The checksum was 9,180,000 in every run. The result isolates key generation;
it is not an end-to-end full-text throughput claim. A separate key-digest
comparison matched the baseline for every BMP character, targeted private-use
marker pairs, and 50,000 deterministic mixed strings.
