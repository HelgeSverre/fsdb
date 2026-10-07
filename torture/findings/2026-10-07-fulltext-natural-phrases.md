# Natural-language full-text phrases

Status: open for the default word parser.

Native MySQL 8.4.11 treats quoted natural-language words as an exact phrase
within one indexed column. fsdb currently scores those words independently.
The maintained [oracle](../scripts/ngram-oracle.fsx) includes this distinction
in its `multi_words` corpus.

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

MySQL returns rows 2 and 5. fsdb returns rows 1, 2, and 5. Adding `@100` to
the natural-language query produces the same divergence.

Boolean exact phrases return rows 2 and 5 in both engines. Boolean proximity
`'"mysql security" @100'` returns rows 1, 2, and 5 in both engines: proximity
can span columns, unlike exact phrases.

The ngram parser intentionally treats natural-language quoted input as a
union of ngrams. A correction must preserve that parser-specific behavior.
No failure signature is enrolled in the known-gaps ledger.
