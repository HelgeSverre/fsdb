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

## Binary phrase verification

Reproduce with `python3 torture/scripts/fulltext-binary-phrase-oracle.py`.
The same expected rows pass through fsdb's SQL wire interface.

Under `utf8mb4_bin` and `utf8mb4_0900_bin`, ordinary word and single-token
quoted queries preserve case. Exact multi-token phrases first require the
original case-sensitive postings, then verify lowercased document text at
positions of the original first query token. Query tokens retain their case.

For the following documents:

| ID | Text |
|---|---|
| 1 | `orchard cobalt` |
| 2 | `Orchard Cobalt` |
| 3 | `ORCHARD COBALT` |
| 4 | `Orchard cobalt orchard` |
| 5 | `orchard Cobalt cobalt` |
| 6 | `orchard cobalt Orchard Cobalt` |
| 7 | `zzzz` |

Natural and Boolean `"orchard cobalt"` match rows 1, 5, and 6. Row 5 has the
required lowercase postings and lowercasing makes the adjacent `Cobalt`
match. Row 4 has both postings, but its lowercase `orchard` occurs after
`cobalt`; verification does not start at the uppercase `Orchard`.

`"Orchard cobalt"`, `"orchard Cobalt"`, and `"Orchard Cobalt"` match nothing
in natural, Boolean, and expansion modes. Single-token `"Orchard"` matches
rows 2, 4, and 6. Boolean proximity `"Orchard Cobalt" @10` matches rows 2
and 6 because proximity uses the original postings without exact phrase
verification.

Boolean ngram queries spanning multiple grams use the same exact verifier;
`Orchard` fails while the single-gram quoted query `"Or"` matches rows 2, 4,
and 6. Natural ngram queries continue to union original gram postings.

The edge matrix also verifies that lowercasing cannot create a missing
candidate posting, internal unindexed short words participate in lowercase
verification, and accented document letters fold case without losing accents.
fsdb shares this verifier between natural word phrases and Boolean phrases,
retaining the original token keys for candidate selection and proximity.
