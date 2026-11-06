# General collation punctuation and subscript equality

An expanded U+2000–U+20FF comparison against MySQL 8.4.11 found 25
equality mismatches among 58 characters with distinct NFKC expansions
under `utf8mb4_general_ci`. MySQL keeps the written forms of selected
punctuation, prime, subscript, and rupee-sign characters distinct from
their expansions. Fsdb had equated them through ICU. The same range's
1,074 two-column ngram searches at size 1 already matched.

The general collation now assigns collision-escaped SQL comparison keys
to those 25 forms, while its full-text token keys retain the previously
audited behavior. A focused regression checks equality, index keys, and
literal-marker isolation. The maintained oracle matches all 580 equality
checks and 10,740 searches across ngram sizes 1–10 after the fix.

Command: `uv run --with pymysql torture/scripts/ngram-multicol-oracle.py
--legacy-range 2000:2100 --collation utf8mb4_general_ci` after
building `src/Fsdb.Cli` in Release. The range and search shapes are bounded;
this is not a complete audit of general-collation weights.
