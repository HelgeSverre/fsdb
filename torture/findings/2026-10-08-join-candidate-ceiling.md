# Join candidate ceiling rejects small results

Native MySQL 8.4.11 accepts a join between two 1,001-row tables containing
integers 0 through 1,000 with `ON a.n+b.n<0`. There are 1,002,001 candidate
pairs and no matches. COUNT(*) returns zero for the inner join and 1,001 for
either left or right outer join. `join-candidate-ceiling-oracle.py` pins these
results using the disposable native server with 64 MiB buffer pool and redo.

At `996cfa2b`, fsdb returns 1105/HY000 for all three queries:
`Join exceeds the 1000000-row candidate limit`.

The generic join traversal retains only accepted candidates and already
checks query cancellation. The arbitrary candidate count is therefore not a
bound on retained result memory: these examples fail despite retaining at
most the unmatched outer rows. The shared traversal removes the candidate ceiling while preserving
first-error handling, cancellation checks, and outer-row padding.

The native fixture also checks constant-key equality joins with a rejecting
residual, with and without an index, plus a non-equi join returning one row
under LIMIT. The LIMIT projection is constant because row selection without
ORDER BY is not deterministic across execution plans. The regression suite
checks cancellation after more than one million rejected pairs. Accepted
results still consume memory; this change does not introduce disk spilling.

Validation: 3,085 Expecto tests pass; 63 wire contracts cover 8,345 steps
with zero differences against native MySQL 8.4.11. Contract artifacts:
`torture/artifacts/runs/20261008T074201090-43018/contracts`.
