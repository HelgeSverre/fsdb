# Correlated binary LIKE prefix audit

MySQL 8.4.11 returned counts of 10 and 10 for two outer patterns (`a01%`
and `a99%`) against 1,000 indexed `utf8mb4_bin` codes. The FSDB regression
returns the same counts and bounds actual residual evaluation to fewer than
60 calls. The existing single-table range path already resolves the outer
pattern for these queries; a proposed second correlated-prefix lookup passed
the same test with and without the new code, so it was removed.

The initial measurement included metadata validation, which evaluates a
subquery with a synthetic outer row before the actual correlated probes. That
probe could not bind either prefix and scanned roughly 1,000 rows. An actual
outer row with a NULL pattern caused another 1,000-row scan. MySQL returns zero
matches for the NULL pattern. FSDB now recognizes a top-level conjunct whose
direct inner column is `LIKE` or `NOT LIKE` a stable NULL outer pattern and
supplies an empty candidate set. Before the `NOT LIKE` extension, its focused
physical regression evaluated a leading residual 2,002 times despite finding
no rows. The ordinary predicate validation still runs: an unknown inner
column returns 1054, and a `LIKE` inside `OR` cannot suppress matches from its
other branch. The `NOT LIKE` oracle checks physical, pass-through derived, and
materialized forms, including an OR branch that still matches beside the NULL
pattern. The regression now counts metadata and runtime calls together, and
remains below 60 for the three positive-LIKE outer rows and each NULL
`NOT LIKE` source.
