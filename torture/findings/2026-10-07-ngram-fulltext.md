# Ngram full-text parser

Status: ngram startup sizes 1–10 are implemented and validated against MySQL
8.4.11. MeCab remains unimplemented.

## Supported behavior

`WITH PARSER ngram` selects overlapping tokens of the configured size for
FULLTEXT indexes created through CREATE TABLE, CREATE INDEX, or ALTER TABLE. Parser
identity survives CREATE TABLE LIKE, SHOW CREATE TABLE, WAL recovery, and
snapshot recovery. DML maintains immutable postings, including rollback.
Unknown parsers produce error 1128 / HY000; parser clauses on ordinary indexes
produce syntax error 1064 / 42000.

Natural-language queries union their ngrams; boolean contiguous terms become
phrases. Against documents containing `生日快乐`, `生日`, `快乐`, and `日快`,
natural-language `生日快乐` matches all four, while boolean `生日快乐` matches
only the complete string. Boolean `生*` searches token prefixes; a wildcard on
the longer term `生日快乐*` retains phrase behavior. Query expansion uses the
selected tokenizer and its searchable seed terms.

Document tokenization preserves whole Unicode characters. ASCII punctuation
breaks token windows, while non-ASCII punctuation can remain in ngrams.
Quoted boolean input first follows word boundaries, so `"生日，快乐"` matches
`生日 快乐`, not the document containing the fullwidth comma.

Stopped tokens remain in phrase positions even when omitted from postings:
boolean `cdabef` must not match `cdef` or `cd ab ef`. Natural-language query
terms are not filtered the same way as indexed tokens: stopped `ab` can match
an indexed collation-equivalent `áb`. Explicit short quoted words remain
required; leading stopped words are discarded. Thus `"生 生日"` finds no
matches in the birthday corpus, while `"a 生日"` matches birthday documents.

Exact phrases stay within one indexed column. Ngram phrases ignore the
proximity suffix, including `@100`. Ordinary word-parser proximity searches
can span columns. Row-wide term counts still determine relevance.
Natural-language word phrases have separate
[oracle evidence](2026-10-07-fulltext-natural-phrases.md).

`@@ngram_token_size` and its GLOBAL form report the configured startup size
(2 by default). The SESSION read and both SESSION and GLOBAL assignments
produce MySQL's error 1238 / HY000. Explicit
scope spelling is preserved in result labels.

## Evidence

The maintained [oracle](../scripts/ngram-oracle.fsx) uses native MySQL 8.4.11,
a disposable database, default token size 2, and enabled default stopwords.
It asserts ordered results, parser DDL errors, variable scope and labels,
mutation behavior, and field boundaries for both parsers.

The [multi-column oracle](../scripts/ngram-multicol-oracle.py) compares 102
natural-language, boolean, and query-expansion searches at each startup size
from 1 through 10. All 1,020 query results agreed with native MySQL 8.4.11
on the audited mixed Chinese, Japanese, Korean, ASCII, punctuation, emoji,
supplementary-character, and NULL corpus. This checks matching row IDs and
query errors, not exact relevance scores or arbitrary CJK text.

The `ngram-fulltext` differential contract covers natural, boolean, and
query-expansion searches through text and prepared protocols, Unicode and
stopword boundaries, DDL alternatives, mutations, and rollback. Focused
Expecto regressions cover these rules and WAL/snapshot recovery.

Validation on 2026-10-07: `just check` passes 2,857 tests with no build
warnings or errors. The contracts lane passes 44 cases and 4,834 steps with
zero differences; its manifest is
`torture/artifacts/runs/20261007T012327847-37527/contracts/manifest.json`.
The maintained native oracle also passes. No differences are added to the
known-gaps ledger.

## Remaining scope

MeCab is not available in fsdb or in either local MySQL oracle installation:
the installed native MySQL 8.4.11 and the digest-pinned torture image contain
no MeCab plugin binary.
The tested parser clause precedes index visibility; broader index-option
ordering is not established by these results.
