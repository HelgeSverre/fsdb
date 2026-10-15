# Correlated binary LIKE prefix audit

MySQL 8.4.11 returned counts of 10 and 10 for two outer patterns (`a01%`
and `a99%`) against 1,000 indexed `utf8mb4_bin` codes. The FSDB regression
returns the same counts and bounds actual residual evaluation to fewer than
60 calls. The existing single-table range path already resolves the outer
pattern for these queries; a proposed second correlated-prefix lookup passed
the same test with and without the new code, so it was removed.

The initial measurement included metadata validation, which evaluates a
subquery with a synthetic outer row before the actual correlated probes. That
probe could not bind either prefix and accounted for roughly 1,000 additional
calls to the test scalar. The regression counts only execution outside metadata
validation to measure index candidate selection. Metadata-probe work is a
separate setup cost and remains an area for planner work.
