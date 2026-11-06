# Unicode 5.2 account-of signs in ngram search

The U+2100–U+21FF two-column oracle against MySQL 8.4.11 exposed a
collision under `utf8mb4_unicode_520_ci`: `℁` (U+2101) and `⅍`
(U+214D) shared an ICU primary key in fsdb. MySQL distinguishes them in
SQL equality and full-text postings. At ngram size 1, the baseline had
12 search differences among 1,512 queries and one cross-form equality
difference among 106 checks. The search differences included membership
and relevance in natural and query-expansion modes.

The Unicode 5.2 fold now gives U+214D a collision-escaped key. The focused
regression checks SQL equality, index keys, full-text keys, and literal
marker isolation. The maintained oracle reports zero differences in 15,120
search queries and 1,060 equality checks across ngram sizes 1–10 after the fix.

Command: `uv run --with pymysql torture/scripts/ngram-multicol-oracle.py
--legacy-range 2100:2200 --collation utf8mb4_unicode_520_ci`
after building `src/Fsdb.Cli` in Release. This is bounded evidence for
the selected range and two-column search corpus.
