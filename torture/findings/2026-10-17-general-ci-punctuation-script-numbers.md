# `utf8mb4_general_ci` punctuation and script-number weights

A two-column full-text corpus covering printable U+2000–U+20FF characters
found false matches under `utf8mb4_general_ci`. At ngram size 1, 276 of
1,074 searches differed from native MySQL 8.4.11; at size 2, 348 differed.
The affected characters were dashes U+2010–U+2011, curly quotes U+2018–U+201F,
and selected superscript and subscript forms U+2070–U+208E and U+2099. ICU
merges some of their primary weights, while MySQL keeps the written forms
distinct. For example, MySQL does not equate `‐` and `‑`, or `⁰` and `₀`.

Native MySQL `WEIGHT_STRING` checks established distinct, literal primary
weights for U+2010–U+201F, U+2070–U+208F, and U+2099, including unassigned
positions within those ranges. Fsdb now preserves these weights in the shared
general-collation key path. A focused regression checks distinct keys and
representative equality cases, including literal marker escaping.

After the change, both ngram sizes have zero membership or relevance
differences across their 1,074 searches:

```sh
dotnet build src/Fsdb.Cli/Fsdb.Cli.fsproj -c Release
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 2000:2100 --collation utf8mb4_general_ci 1 2
```

Other Unicode blocks and full-text query shapes remain outside this audit.
