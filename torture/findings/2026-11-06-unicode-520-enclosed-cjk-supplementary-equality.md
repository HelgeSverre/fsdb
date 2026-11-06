# Unicode 5.2 supplementary enclosed CJK equality

The U+1F200–U+1F2FF two-column oracle against MySQL 8.4.11 found 14 SQL
equality differences among 58 characters with distinct NFKC expansions
under `utf8mb4_unicode_520_ci`. MySQL expands enclosed kana U+1F200 and
U+1F213, but keeps the written weights of U+1F232–U+1F23B and
U+1F250–U+1F251 distinct from their ideographs. Fsdb had the opposite
comparison behavior for those forms. All 384 ngram searches at size 1
already matched.

The Unicode 5.2 SQL fold now expands those two kana and assigns
collision-escaped comparison keys to the twelve ideographs. The full-text
key retains its audited fold. A focused regression checks equality,
index keys, and literal-marker isolation.

The oracle reports zero differences across 3,840 natural, Boolean, and
query-expansion searches and 580 NFKC equality checks at startup ngram
sizes 1–10.
Run `uv run --with pymysql torture/scripts/ngram-multicol-oracle.py
--legacy-range 1f200:1f300 --collation utf8mb4_unicode_520_ci` after
building `src/Fsdb.Cli` in Release. This bounded corpus does not establish
full Unicode 5.2 collation parity.
