# Full-text stopword configuration

Status: open. Fsdb uses the built-in stopword list unconditionally. Native
MySQL 8.4.11 exposes `innodb_ft_enable_stopword` globally and per session, both
enabled by default. The query and index lifetime rules need to be implemented
together; accepting SET alone would not provide the requested behavior.

Run the [native oracle](../scripts/fulltext-stopword-oracle.py) with `mysqld`,
`mysql`, and `mysqladmin` on PATH:

```sh
python3 torture/scripts/fulltext-stopword-oracle.py
```

It uses a disposable socket-only server and the shared transaction-oracle
client and server harness. Both word and ngram cases pass. Word probes search
`the`; ngram probes search `ab`, which the built-in `a` stopword excludes.

## Captured behavior

Each table starts with a full-text index built under either ON or OFF. The
connection then switches the session setting to the opposite value.

| Operation after switching the setting | Observed behavior |
|---|---|
| Search existing rows | Retains the original stopword behavior |
| Insert another matching row and search | Retains the original stopword behavior |
| Drop and add the same index in one ALTER | Retains the original stopword behavior |
| Drop the index, then add it in a separate ALTER | Adopts the new behavior |
| Rebuild with ENGINE=InnoDB | Adopts the new behavior |
| Add a second full-text index over a populated new column | Both indexes adopt the new behavior in this case |

The oracle checks both ON-to-OFF and OFF-to-ON transitions. Under OFF, searches
retrieve the tested stopword-containing text. Under ON, they return no rows.
These observations concern the explicit DDL shapes above; they do not establish
rules for every ALTER algorithm or multiple pre-existing full-text indexes.

## Implementation boundary

The full-text module now carries an explicit `StopwordPolicy` with indexes,
documents, and read views. Its internal constructor supports the built-in list or
disabled filtering. Natural and boolean parsing, prefix postings, phrase term
selection, and query-expansion seeds use that policy. Document policy also governs
removal and seed selection, preserving the rules used to build stored postings.

Focused tests cover word and ngram searches, optimized dictionary paths, writes,
removal, prefix maintenance, and retained minimum word length. The native oracle
also verifies natural-language and query-expansion behavior. Production storage
constructors still select the built-in policy; session settings, DDL policy
selection, and WAL configuration context have not yet been connected.

Snapshot format 14 (`FSNE`) stores one policy byte per full-text index in index
definition order, including indexes without rows. Recovery combines that policy
with each document's historical tokenizer. Tests cover mixed word/ngram policies,
empty and populated indexes, and writes after recovery. A fixture produced by the
format-13 writer verifies that `FSND` loads with built-in stopwords and retains
its ngram postings. Earlier formats remain readable; older binaries cannot read
`FSNE` snapshots.

The effective filtering policy must be retained with the index state and used by
both writes and queries. DDL must preserve or replace that state according to the
observed operation, and WAL/snapshot recovery must reconstruct the same searchable
postings. Existing historical ngram-tokenizer handling provides a related model,
but its preservation rules must not be assumed to cover stopword configuration.

Custom stopword tables, global-setting inheritance, persistence across reopening,
and additional ALTER variants have not yet been probed by this oracle. Runtime
configuration remains open; no known-gap suppression is included.

## Verification

The native stopword oracle passes. `just check` passes all 2,885 tests without
build warnings or errors. The natural-phrase oracle and all 47 contracts
(5,007 steps) pass without differences:
`torture/artifacts/runs/20261007T051057405-76759/contracts`.

The durability lane passes with 12 crash restarts and all 67 acknowledged commits
recovered, including checkpoint, WAL-tail, and torn-tail checks:
`torture/artifacts/runs/20261007T051145109-76815/durability-seed101-workers4-ops100-restarts8-checkpoint16`.
