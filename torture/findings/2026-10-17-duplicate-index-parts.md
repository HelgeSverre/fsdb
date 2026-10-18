# Duplicate plain columns in index definitions

The imported finding “Covered-key probe can skip an equality on duplicate indexes” depends on fsdb accepting `KEY dup_a(a,a)`. The planner treats the two physical positions as two covered predicates, which can drop another equality from the residual join condition. MySQL 8.4.11 rejects the index definition itself with error 1060 (`Duplicate column name 'a'`), so the compatible fix is at DDL validation.

The pinned MySQL image (`mysql:8.4.11@sha256:b3b90af2a6552ae30c266fdb7d5dd55f3afb72404bb78d37fe8a23eb857fd3fb`) returned 1060 for repeated plain columns in `CREATE TABLE`, `CREATE INDEX`, `ALTER TABLE ... ADD KEY`, `ADD UNIQUE KEY`, and `ADD PRIMARY KEY`. It reports the repeated spelling (`A` for `(a,A)`) and rejects repeated columns with different prefix lengths. It accepts `KEY ix(a,(LOWER(a)))` and repeated functional expression parts, so the validation does not treat those as duplicate plain columns.

Fsdb now checks plain key-part names case-insensitively in the shared index validation and in the `ADD PRIMARY KEY` action. The focused test failed against `origin/main`, where `CREATE TABLE ... KEY dup_a(a,a)` succeeded; it passes with the validation. This closes the stated join path by refusing its incompatible index definition, while leaving other join-planning findings to separate audits.
