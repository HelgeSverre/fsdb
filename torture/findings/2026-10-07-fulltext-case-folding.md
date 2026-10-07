# Full-text case folding

Native oracle: MySQL 8.4.11, disposable local server. Reproduce with
`python3 torture/scripts/fulltext-case-folding-oracle.py`.

Full-text matching folds case under `utf8mb4_0900_as_cs`, despite ordinary
SQL comparisons being case-sensitive. The same corpus under `utf8mb4_bin`
and `utf8mb4_0900_bin` retains case-sensitive ordinary word lookups.

With stopwords disabled, documents `orchard cobalt`, `Orchard Cobalt`, and
`ORCHARD COBALT` all match `orchard`, `Orchard`, and `ORCHARD` under both
`utf8mb4_0900_ai_ci` and `utf8mb4_0900_as_cs`. This holds for word and size-2
ngram indexes in natural, Boolean, and expansion modes. Quoted phrases and
Boolean prefixes also fold case. Deleting or replacing documents removes
both their exact and prefix postings.

Case folding retains accent distinctions under `utf8mb4_0900_as_cs`:
`CAFE` matches only `cafe`, while `café` matches both `café` and `CAFÉ`.
With those three documents plus a nonmatching fourth document, the accented
term's native score is `0.0906190574169159`, approximately `log10(4/2)^2`.
Case variants share document frequency.

fsdb normalizes nonbinary full-text tokens before constructing exact and
prefix keys. Index construction, query parsing, document removal, and
recovery use that same normalization. Ordinary SQL collation behavior is
unchanged.

## Remaining binary phrase behavior

A broader native probe with the same orchard corpus found that quoted
`"Orchard Cobalt"` matches no word-indexed rows under either binary
collation, although ordinary `Orchard` matches the mixed-case row. Binary
ngram Boolean queries `Orchard` and `ORCHARD` likewise match no rows,
while lowercase `orchard` matches the lowercase row. Natural ngram queries
still union their case-sensitive gram postings: `orchard` and `Orchard`
each match the lowercase and mixed-case rows, while `ORCHARD` matches the
uppercase row. Binary phrase verification needs a separate regression and
implementation; the maintained case-folding oracle covers nonbinary phrase
matching and binary ordinary word/prefix matching.
