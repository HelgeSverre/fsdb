# Correlated decimal inequality candidates

Native MySQL 8.4.11 returns `(50, 0, 999)` for outer decimal bounds
`(1.25, NULL, 51.25)` against an indexed inner table with 950 rows at
`k=1.25` and 50 rows at `k=2.25..51.25`. Both `d.k <> o.k` and
`NOT (d.k = o.k)` have those counts; adding `OR d.id=500` returns
`(51, 1, 999)`. The [native oracle](../scripts/correlated-inequality-oracle.py)
checks direct, pass-through derived, and stable materialized derived sources.

FSDB returned the same rows before the planner change, but a selective
`TOUCH(d.id)=d.id` residual ran 2,002 times for the direct source. The
planner now unions strict `<` and `>` indexed ranges for a direct DECIMAL
inner column and a stable DECIMAL or NULL bound. The original inequality
still filters candidate rows. A focused regression keeps residual calls
below 80 for direct, pass-through, and materialized sources, including a
complete OR branch and NULL bound; `IGNORE INDEX` restores over 900 calls.

The integer path remains intact. Mixed numeric types, text, functional
index expressions, and other inequality forms still need separate domain
evidence before the same candidate rewrite can be used.
