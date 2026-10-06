# Offset RANGE frames and NULL ordering keys

MySQL 8.4.11 treats an offset from a NULL ordering key as the NULL peer group.
An unbounded boundary still reaches the partition edge. For a non-NULL current
key, NULL rows belong to an unbounded preceding edge in ascending order and
an unbounded following edge in descending order. Finite offsets do not include
NULL keys merely because their numeric boundary passes the last non-NULL key.

With keys NULL, NULL, 1, 3, 3, 7 and numeric text inputs 1x, 2x, 4x, 8x, 16x,
32x, an ascending RANGE frame from UNBOUNDED PRECEDING to 1 PRECEDING returns
3, 3, 3, 7, 7, 31. Dropping NULL keys from each numeric frame incorrectly
returns 3, 3, NULL, 4, 4, 28. Reversing the ordering also requires a prefix
for a current NULL row to include the preceding non-NULL rows.

SUM/AVG accumulation for stable growing RANGE frames consumes each input once.
This preserves MySQL's conversion warning multiplicity, including tied keys and
empty initial frames. Suffix and bounded frames continue evaluating their own
inputs, retaining repeated conversion warnings. Frame-boundary lookup still
scans the partition; no measured complexity or speed improvement is claimed.

`torture/scripts/range-warning-oracle.fsx` asserts numeric frame values and
ordered warnings against native MySQL 8.4.11 over text and prepared protocols.
The `offset-range-aggregates` differential contract also compares numeric and
temporal offsets, both directions, and mixed, absent, or exclusively NULL keys.
The Expecto regression pins prefix and suffix values and warning sequences.

Validation: `just check` builds without warnings and passes 2,835 tests. Native
MySQL 8.4.11 passes the RANGE and prepared-type oracle scripts. The differential
run passes 3,769 steps across 41 cases with zero differences; its manifest is
`torture/artifacts/runs/20261006T215022801-1511/contracts/manifest.json`.
The disposable native server is shut down after validation.
