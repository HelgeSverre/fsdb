# Full-text stopword configuration

Status: implemented for GLOBAL/SESSION `innodb_ft_enable_stopword` and the
maintained word/ngram DDL matrix. The setting defaults to ON. Indexes retain their
captured policy across later session changes, writes, WAL replay, and snapshots.
Startup options seed both variable scopes. Custom stopword tables remain open.

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
| TRUNCATE, then insert | Adopts the new behavior |
| Add a column | Refreshes the existing full-text policy |
| Add a second full-text index on an existing column | Both indexes retain the table's existing policy |

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
also verifies natural-language and query-expansion behavior. QueryHandler derives
the creation/rebuild setting from session variables before each statement.
Storage preserves existing policies for ordinary writes and metadata-only
changes, while the tested physical rebuilds adopt the current setting.

WAL tag `0x1B` captures disabled stopword filtering around schema events. The
wrapper composes with historical ngram-size context and is stripped from public
commit observers. Legacy schema events retain enabled filtering. Recovery tests
cover both WAL-only and checkpointed histories, including disabled stopwords
combined with a non-default ngram size. Older binaries cannot replay this tag.

Snapshot format 16 (`FSNG`) retains the active policy per full-text index and a
shared table of historical tokenizer/stopword rules. Each row refers to the rules
used for its postings, so reconstruction preserves mixed document histories.
Custom policies include literal source words and their collation. Empty indexes
retain their active policy. Invalid policies, tokenizer values,
rule-table lengths, and document references are rejected.

Format 14 (`FSNE`) supplies its per-index policy to every document; format 13
(`FSND`) supplies built-in filtering. Format 15 (`FSNF`) retains per-document
rules for built-in/disabled policies. Fixtures produced by all three older writers
verify those paths. Earlier formats remain readable; older binaries cannot read
`FSNG` snapshots. The WAL format is unchanged by the snapshot-rule extension.

Regressions verify GLOBAL values seed new sessions without changing existing
sessions, reject invalid boolean values, and retain policy across reopening.
Custom stopword tables and additional ALTER variants remain outside the verified
matrix. No known-gap suppression is included.

## Startup configuration

The [startup oracle](../scripts/fulltext-stopword-startup-oracle.py) checks native
MySQL 8.4.11 option parsing and restarts against the same data directory. Bare
`--innodb-ft-enable-stopword`, `1`, `ON`, and `TRUE` enable filtering; other tested
explicit values, including `2`, `-1`, an empty string, and `yes`, disable it.
Values are case-insensitive but quoted whitespace is significant. The `skip-`
and `disable-` prefixes force OFF; `enable-` forces ON, regardless of the supplied
value. Later options win.

Fsdb accepts these startup options and option-file entries. Command-line options
follow file entries. `Db.withFullTextStopwords` initializes the GLOBAL value used
by new sessions and works before or after `Db.withDataDir`. Restarting with ON
preserves an existing disabled index's searchable stopwords, while a newly
created index uses ON; both native MySQL and fsdb cover this distinction.

## Verification

The startup oracle passes its parsing matrix and OFF/ON/2 restart sequence.
Fsdb command-line smoke checks pass option-file defaults, overriding CLI values,
bare options, both alias orders, and the forced enable/disable prefixes, while
checking new and recovered full-text indexes. `--help` and `--version` succeed.


The stopword matrix passes on native MySQL 8.4.11 and fsdb. `just check` passes
all 2,897 tests without build warnings or errors. The natural-phrase oracle and all 47 contracts
(5,007 steps) pass without differences:
`torture/artifacts/runs/20261007T061108753-84784/contracts`.

The durability validation passes with 12 crash restarts and all 67 acknowledged commits
recovered, including checkpoint, WAL-tail, and torn-tail checks:
`torture/artifacts/runs/20261007T061124411-84821/durability-seed101-workers4-ops100-restarts8-checkpoint16`.
