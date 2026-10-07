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

fsdb's in-memory full-text documents retain their indexing tokenizer separately
from the active tokenizer for queries and future writes. Removal and replacement
use that historical tokenizer when clearing prefix postings. Snapshot format
FSND retains each indexed row's tokenizer and reconstructs
postings with that historical context. Nondefault-size WAL writes retain their
indexing context in an event wrapper; legacy records continue to mean size 2.
Changing only the startup constant would silently retokenize historical data
and disagree with the observed recovery behavior.

Completing configurable sizing requires startup-option integration and
validation of metadata-only DDL across size changes. Prepared-XA publication
retains each document's recorded tokenizer through live commit, WAL replay,
and snapshots. The [prepared-XA oracle](2026-10-07-ngram-xa-recovery.md) records
missing pending ngram postings after MySQL restart even without a token-size
change; fsdb preserves those postings. Index rebuilds must deliberately replace
historical context. The same distinction
will matter when implementing configurable stopwords. Default-size snapshots
must remain readable, and reported system variables must agree with the
active startup configuration.

## In-memory regression

`ngram size changes retain historical postings during writes` in
`FullTextTests.fs` starts with size-2 documents, selects size 3 for queries and
future writes, then inserts, deletes, and replaces rows. Natural-language
three-character queries find newly indexed rows; short boolean words and
quoted words still find size-2 postings. Prefix searches stop finding deleted
or replaced historical rows. The test failed on short boolean lookup before
the document/query distinction was implemented.

The maintained native oracle also checks quoted historical lookup and prefix
removal inside a rolled-back transaction. This covers the engine foundation;
startup options remain open.


## Snapshot recovery

FSND (format 13) stores one tokenizer byte per full-text index per row, in
index-definition order. Rows without full-text indexes retain their previous
encoding. The row stream and checksum still cover the tokenizer bytes; no
separate unbounded posting buffer is written. FSNC and earlier snapshots
remain readable. Older fsdb binaries cannot read FSND snapshots.

Recovery rebuilds full-text postings once from the rows and their saved
context, then preserves the postings maintained by WAL replay. The
`snapshots retain mixed ngram document tokenizers` regression covers multiple
indexes with different declaration and name order, mixed token sizes,
snapshot-of-snapshot recovery, and a subsequent WAL-tail insert. It failed
before tokenizer metadata was retained. A separate regression verifies FSNC
row recovery without tokenizer metadata.

Startup-option integration remains open. Historical snapshot
and WAL formats predate configurable sizing and must continue to mean size 2,
even when a future server starts with another configured size.

## WAL recovery

Nondefault-size row and schema events carry their indexing size in WAL tag
0x1A. Replay applies that size to the affected index for new documents without
changing historical postings. Unwrapped events retain size-2 semantics. The
wrapper is removed before observer delivery and traversed when filtering
session-local temporary-table events. Default-size event encoding is unchanged;
older binaries cannot read the new wrapper.

The WAL regression covers a committed size-3 transaction following size-2
writes, rollback with an auto-increment reservation, a newly created size-3
index, temporary-table filtering, observer delivery, and snapshot recovery of
the replayed state. Replaying counter advances updates the counter directly,
so it does not rebuild historical full-text postings through ALTER TABLE.

## Validation

On 2026-10-07, `just check` passes 2,862 tests with no build warnings or errors.
The mixed-tokenizer WAL regression also passes independently. Native MySQL
validation passes 45 contract cases and all 4,978 differential steps.

The durability lane with seed 101, four workers, 100 operations per worker,
eight requested restarts, and checkpoint interval 16 passes. All 12 total
crash/restart checks preserve acknowledged commits and transaction boundaries,
including schema state, WAL tail, snapshots, and torn-tail repair. Artifact:
`torture/artifacts/runs/20261007T021828277-47159/durability-seed101-workers4-ops100-restarts8-checkpoint16`.
