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
most the unmatched outer rows. Removing this compatibility ceiling should
preserve first-error handling, cancellation checks, and outer-row padding.
The same ceiling also exists on indexed and residual hash-join paths; those
need controls before declaring the gap closed.
