# Prepared XA ngram recovery

Status: oracle captured; fsdb cross-size prepared-XA recovery remains open.

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

fsdb still needs an explicit invariant and regression for live prepared-XA
commit, subsequent WAL replay, and snapshot replay across startup-size
changes. Its prepared branch retains historical indexing context in durable
events; publication must not accidentally use a different context merely
because the live store now has a different active tokenizer. The native
oracle does not establish an expectation that historical prepared content
remains searchable in MySQL, and this limitation must not be silently treated
as proof of fsdb parity.

## Verification

On 2026-10-07, `just check` passes 2,864 tests with no build warnings or errors.
Both maintained scenarios pass against native MySQL 8.4.11.
Controls cover commits without restart, detached prepared transactions,
prepared recovery, row visibility, full-text visibility, ANALYZE, a fresh
post-restart write, and explicit index rebuild. Both private server processes
exit and their data directories are removed.

Run with MySQL 8.4.11 tools on PATH:

```sh
python3 torture/scripts/ngram-xa-oracle.py
```
