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

`FullText.isSearchable`, boolean parsing, prefix postings, phrase term selection,
and query-expansion seeds currently use the fixed built-in list. Ngram postings
also omit excluded tokens during index construction. A session-only query filter
would leave inserts, prefix matching, expansion, and existing indexes inconsistent.

The effective filtering policy must be retained with the index state and used by
both writes and queries. DDL must preserve or replace that state according to the
observed operation, and WAL/snapshot recovery must reconstruct the same searchable
postings. Existing historical ngram-tokenizer handling provides a related model,
but its preservation rules must not be assumed to cover stopword configuration.

Custom stopword tables, global-setting inheritance, persistence across reopening,
and additional ALTER variants have not yet been probed by this oracle. No runtime
support or known-gap suppression is included in this change.
