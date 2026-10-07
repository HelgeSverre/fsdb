# Prepared XA ngram recovery

Status: fsdb tokenizer consistency fixed; MySQL recovery divergence documented.

The [native MySQL 8.4.11 oracle](../scripts/ngram-xa-oracle.py) compares a
restart at token size 2 with a restart from size 2 to size 3. It uses private
server directories and checks the version and active token size after each
start.

## Observed behavior

The corpus has five identical `生日快乐` documents introduced in this order:

1. A normal committed insert.
2. An XA insert prepared and committed in the same connection.
3. An XA insert prepared in one connection and committed in another.
4. An XA insert prepared before shutdown and committed after restart.
5. A normal insert after restart.

Inside the first XA transaction, an ordinary SELECT sees the new row before
commit, while MATCH still sees only document 1. After commit, document 2 is
searchable. This agrees with MySQL's documented [full-text transaction
handling](https://dev.mysql.com/doc/refman/8.4/en/innodb-fulltext-index.html#innodb-fulltext-index-transaction-handling): insert and update index work is
processed at commit time.

Before restart, documents 1–3 are searchable. After restart, `XA RECOVER`
reports the fourth transaction. Committing it makes row 4 visible to an
ordinary SELECT, but its ngram posting is absent. Boolean `生日` still returns
only 1–3. ANALYZE TABLE does not repair the missing posting.

This happens both when token size remains 2 and when it changes to 3. It is
therefore not evidence that MySQL retokenizes prepared writes using the new
startup value. Exploratory checks also found the posting still absent after
three one-second waits.

A normal post-restart insert is searchable. At size 2, natural-language
`生日快` finds 1, 2, 3, and 5. At size 3 it finds only 5, because the older
committed postings retain their size-2 tokens. Separate DROP INDEX and ADD
FULLTEXT statements rebuild the index; the query then finds all five rows,
including row 4.

## Consequence for startup integration

Ordinary commits demonstrate preserved historical postings, but recovered
prepared XA commits have a separate observable limitation in this MySQL
version. A missing posting cannot identify which tokenizer would have been
used if the posting had survived recovery.

fsdb preserves the indexing tokenizer recorded by each prepared document.
Publishing branch rows uses their historical tokenizers while retaining the
live index's active tokenizer for queries and future writes. If a concurrent
insert occupies a branch's private row identity, publication maps the new row
identity back to the source document when reading its tokenizer.

The regression first found different natural-language results after live XA
commit and WAL recovery. It covers both 2-to-3 and 3-to-2 transitions, prepared
branches recovered from WAL or snapshots, inserted and updated documents,
concurrent inserts that reassign private row identities, ordinary word indexes,
subsequent writes, and a snapshot of the recovered committed state.

Recovered prepared documents remain indexed in fsdb. This differs from the
observed MySQL 8.4.11 posting loss. The consistency fix preserves fsdb's
recorded indexing context; it does not establish parity with that limitation.
Public startup-option integration remains open.

## Verification

On 2026-10-07, `just check` passes 2,865 tests with no build warnings or errors.
All 47 compatibility cases and 5,007 differential steps pass. Manifest:
`torture/artifacts/runs/20261007T030424544-56822/contracts/manifest.json`.
The durability lane with seed 101, four workers, 100 operations per worker,
eight requested restarts, and checkpoint interval 16 passes all 12 crash checks.
Artifact:
`torture/artifacts/runs/20261007T030512176-56966/durability-seed101-workers4-ops100-restarts8-checkpoint16`.

Both maintained scenarios pass against native MySQL 8.4.11.
Controls cover commits without restart, detached prepared transactions,
prepared recovery, row visibility, full-text visibility, ANALYZE, a fresh
post-restart write, and explicit index rebuild. Both private server processes
exit and their data directories are removed.

Run with MySQL 8.4.11 tools on PATH:

```sh
python3 torture/scripts/ngram-xa-oracle.py
```
