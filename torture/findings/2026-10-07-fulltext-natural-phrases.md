# Natural-language full-text phrases

Status: fixed and validated against native MySQL 8.4.11.

The default word parser treats quoted natural-language words as an exact
phrase within one indexed column. Ordinary terms and separate quoted phrases
are alternatives. Internal short words and stopwords retain their positions;
leading unsearchable words are skipped. An unmatched quote leaves ordinary
word searches. Ngram natural-language queries retain their union behavior.

```sql
CREATE TABLE docs (
  id INT PRIMARY KEY, a TEXT, b TEXT, FULLTEXT(a,b)
);
INSERT INTO docs VALUES
  (1,'mysql','security'),
  (2,'mysql security',''),
  (3,'mysqlsecurity',NULL),
  (4,'my','sql'),
  (5,'sql','mysql security');
SELECT id FROM docs
WHERE MATCH(a,b) AGAINST('"mysql security"' IN NATURAL LANGUAGE MODE)
ORDER BY id;
```

MySQL returns rows 2 and 5. The failing fsdb regression returned rows 1, 2,
and 5 before applying phrase constraints. Boolean exact phrases follow the
same column boundary. Boolean proximity can span columns.

## Scoring and expansion

Phrase matching selects which term contributions are eligible for each row.
Scoring counts each matching document's term frequency once, while repeated
query words multiply their posting document frequency before computing IDF.
For example, with ten documents and seven containing `mysql`, two query
occurrences use `log10(10/14)^2`, rather than doubling `log10(10/7)^2`.

Query expansion preserves the original phrase clauses. Only terms that
contributed to the first pass are already searched; other seed terms become
additional ordinary alternatives. In the example, expansion adds `sql` from
row 5 and therefore includes row 4, but still excludes row 1.

A numeric suffix such as `@100` is an ordinary natural-language search term,
not a proximity control. A document containing `100` can therefore match it.
The boolean parser remains responsible for interpreting proximity syntax.

## Evidence

The maintained [oracle](../scripts/natural-phrase-oracle.fsx) uses a disposable
MySQL 8.4.11 database and checks mixed terms, multiple phrases, stopwords,
short words, unmatched quotes, and expansion. Expecto checks the phrase
boundary and repeated-term relevance, including an indexed row restriction.
The `natural-fulltext-phrases` contract compares matching rows and relevance
rounded to five decimals through text and prepared protocols.

Validation on 2026-10-07: `just check` passes 2,858 tests with no build
warnings or errors. The maintained native oracle passes. The differential
lane passes 45 cases and 4,978 steps with zero differences; the manifest is
`torture/artifacts/runs/20261007T013106585-39177/contracts/manifest.json`.
No differences are enrolled in the known-gaps ledger.
