# Text range boundaries must include folded-equal values

The imported findings “BETWEEN can miss collation-equivalent indexed rows” and “Folded-collation functional ranges can omit matching rows” identify the same boundary mistake. Secondary-order entries sort by full collation order, which breaks ties among values that SQL comparisons treat as equal under `ComparePrimary`. A binary search using full order can start or end inside one folded-equivalence class. The residual predicate cannot recover skipped rows.

The pinned MySQL 8.4.11 image (`mysql:8.4.11@sha256:b3b90af2a6552ae30c266fdb7d5dd55f3afb72404bb78d37fe8a23eb857fd3fb`) returned ids `1,2,3` for both indexed and forced-scan versions of `label BETWEEN 'é' AND 'é'` over rows `('e'),('é'),('E'),('f')` under the default `utf8mb4_0900_ai_ci` collation. It also returned ids `1,2,3` through a functional `INDEX ((LOWER(label)))` for `LOWER(label) >= 'é' AND LOWER(label) < 'f'`; EXPLAIN reported `range` on that key.

Fsdb now finds textual range boundaries using the primary collation comparison when equality is not also full-order equivalence. The entries remain in full order, but primary-equal entries are contiguous, so the boundary search includes the complete class. Binary and other order-equivalent collations retain the existing full-order search. The ordinary WHERE predicate still decides the final result.

The new regression checks SELECT, UPDATE, DELETE, and the functional expression index against an unindexed control. Running that test against `origin/main` failed on the direct `BETWEEN` case (`2` instead of `1,2,3`); it passed with the change. This addresses the two audited index-boundary shapes, not every collation or range-planning case.
