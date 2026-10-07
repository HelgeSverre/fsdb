# Ngram startup sizing and recovery

Status: open. fsdb uses token size 2 and rejects `--ngram-token-size`.

The native MySQL 8.4.11 [oracle](../scripts/ngram-size-oracle.py) starts a
private disposable server, retains its data directory across restarts, and
checks index behavior before and after writes and explicit rebuilds.
It also checks the minimum and maximum token sizes with a CJK corpus.

## Startup contract

`--ngram-token-size` selects the GLOBAL, read-only `ngram_token_size` value.
Sizes 1, 2, 3, and 10 are accepted. A requested value of 0 is clamped to 1;
11 is clamped to 10. Runtime assignment remains a read-only-variable error.

## Recovery contract

An index created at size 2 retains its old postings after restart at size 3.
For documents `生日快乐` and `生日`, natural-language query `生日快` therefore
finds no rows after that restart. Inserting another `生日快乐` then makes only
the new row match: the same index contains postings made with different
startup sizes.

Boolean `生日` still finds the old size-2 postings after restart at size 3,
even though natural-language `生日` does not. Query tokenization and stored
posting eligibility therefore cannot share one unconditional length filter.

Separate `ALTER TABLE ... DROP INDEX` and `ALTER TABLE ... ADD FULLTEXT ...
WITH PARSER ngram` statements rebuild all rows at the current size. The
size-3 `生日快` query then matches all `生日快乐` rows. A combined DROP/ADD
statement with the same definition did not rebuild the postings in the
exploratory MySQL run; the maintained oracle uses separate statements.

## Implementation implications

fsdb's `FullText.Index` currently owns one tokenizer for both document and
query processing. Recovery reconstructs all postings from rows, and neither
WAL nor snapshots retain the tokenization context of individual indexed rows.
Changing only the startup constant would silently retokenize historical data
and disagree with the observed recovery behavior.

Configurable sizing needs a distinction between current query tokenization
and persisted indexing state, including mixed generations after new writes.
Index rebuilds must deliberately replace that state. The same distinction
will matter when implementing configurable stopwords. Default-size snapshots
must remain readable, and reported system variables must agree with the
active startup configuration.
