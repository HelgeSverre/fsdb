# Custom full-text stopword tables

Status: runtime SQL selection and recovery implemented for InnoDB sources.
`innodb_ft_user_stopword_table` and `innodb_ft_server_stopword_table` validate
references, select captured lists, and retain index sources across recovery.
The [native oracle](../scripts/fulltext-custom-stopword-oracle.py) records
validation, precedence, collation, index lifetime, and restart behavior on
MySQL 8.4.11. It uses disposable native servers and data directories:

```sh
python3 torture/scripts/fulltext-custom-stopword-oracle.py
```

## Variable validation

Both variables default to NULL. The server variable has GLOBAL scope; the user
variable has GLOBAL and SESSION scopes. Setting the user variable globally seeds
new sessions without changing existing sessions. `SHOW` renders NULL table
settings as empty text and reads the current GLOBAL server source. Invalid
SESSION assignment to the server variable reports the scope error before
validating the value. A session source does not require SELECT privilege on
the source table, as verified with a separate account.

| Operation | Native result |
|---|---|
| Read SESSION server variable | Error 1238, HY000 |
| Set SESSION server variable | Error 1229, HY000 |
| Assign NULL | Accepted |
| Assign an empty string, missing table, or dotted `database.table` name | Error 1231, 42000 |
| Assign an integer | Error 1232, 42000 |
| Reference an InnoDB table with first column `value VARCHAR(...)` | Accepted |
| Add columns after `value` | Accepted |
| Put another column before `value` | Error 1231, 42000 |
| Name the column uppercase `VALUE` | Error 1231, 42000 |
| Use CHAR, TEXT, INT, or VARBINARY for `value` | Error 1231, 42000 |
| Reference a MyISAM table, temporary table, or view | Error 1231, 42000 |

The MyISAM validation row describes native MySQL. Fsdb's existing
[storage-engine substitution](../../GAPS.md) uses its InnoDB-shaped store for
recognized engine names, so a table requested as MyISAM is still an InnoDB
source in fsdb. The wire comparison covers the InnoDB validation matrix and
keeps this engine boundary explicit.

The [MySQL variable documentation](https://dev.mysql.com/doc/refman/8.4/en/innodb-parameters.html#sysvar_innodb_ft_user_stopword_table)
describes a single VARCHAR column named `value` and the `database/table` reference
syntax. The native oracle establishes the more permissive behavior for trailing
columns, and the case-sensitive requirement for the first column's name.

## Startup selection

The [startup oracle](../scripts/fulltext-stopword-startup-oracle.py) verifies
`--innodb-ft-user-stopword-table` and `--innodb-ft-server-stopword-table` against
native MySQL 8.4.11. Startup stores literal names without validating the source:
the table can be created after the server starts. A missing argument supplies
NULL, `=` supplies an empty string, and `=NULL` supplies the four-character text.
Invalid sources fall back to built-ins when an index is built; an invalid user
source still bypasses a valid server source. Later values override earlier ones.

Fsdb accepts the same command-line options and option-file entries.
`Db.withFullTextStopwordTables` seeds the variables before sessions are opened,
both before and after `Db.withDataDir`. Startup variables are not persisted;
index sources and historical postings have their own recovery lifetime.
The executable smoke matrix checks both source flags, empty and absent values,
repeated arguments, option-file settings, CLI overrides, and recovery against
the same expected scopes and search results. `--help` and `--version` succeed.
Argu's empty equals-assignment boundary has a parser regression.

## Precedence and capture

Enabled filtering uses the SESSION user table when configured, otherwise the
GLOBAL server table, otherwise the built-in list. Disabled filtering bypasses
both custom lists. An empty custom table replaces the built-in list with an
empty list; it does not add to the default words.

A missing user table after a successful SET falls back to the built-in list,
even when a valid server table is configured. DDL succeeds in this case. Clearing
the user variable to NULL instead restores the server table's precedence.

Words are read when building the index. Updating the source table does not change
existing searches or subsequent inserts into that loaded index. A physical
`ALTER TABLE ... ENGINE=InnoDB` rebuild reads the updated source. Changing the
variable alone therefore cannot implement this feature correctly.

NULL and empty entries have no effect in the tested word corpus. Entries are
whole values, not tokenized phrases: `cobalt red` does not exclude `cobalt`.
Leading/trailing spaces remain significant under the default NO PAD collation.
PAD SPACE source collations retain their padding semantics: a custom `a `
entry under `utf8mb4_bin` excludes `ab` grams, while the same entry under
`utf8mb4_0900_ai_ci` does not. Source collation expansions also apply to
ngram substrings: custom `ss` and `ß` each exclude both `ss` and `ßx` under
`utf8mb4_0900_ai_ci`.
For a size-2 ngram index, stopword `a` excludes `ab`, while the longer stopword
`abc` excludes neither `ab` nor `bc`.

## Collation and phrase behavior

The oracle crosses `utf8mb4_0900_ai_ci`, `utf8mb4_0900_as_cs`, and `utf8mb4_bin`
for both the source column and full-text column. With custom entries `Orchard`
and `café`:

- An accent/case-insensitive source excludes all tested orchard/cafe variants,
  including when the indexed column is binary.
- A case-sensitive source and a nonbinary indexed column retain all three
  orchard variants. The matrix indicates lowercasing before stopword comparison for these
  indexed columns; the uppercase source entry does not exclude them.
- With both source and indexed column binary, `Orchard` is excluded while
  `orchard` and `ORCHARD` remain individually searchable.
- Accent-sensitive source entries distinguish `café` from `cafe`. The full-text
  column's collation still controls which remaining postings a query can match.

Custom words also affect phrase anchors. With `orchard` as a custom stopword,
`"orchard cobalt"` matches all tested documents containing `cobalt`, including
one containing `other cobalt`. The required Boolean query `+orchard +cobalt`
matches nothing. The built-in word `the` becomes searchable because the custom
list replaces the built-in list.

## Restart changes future writes, not old postings

The restart oracle uses the same data directory through this sequence:

1. Configure custom source `probe/words` containing `orchard`. Build an index
   with rows 1=`orchard`, 2=`cobalt`, 3=`the`, and 4=`zzzz`.
2. Replace the source contents with `cobalt`, then restart.
3. Insert row 5=`orchard cobalt the`, drop the source table, and restart again.
4. Insert row 6=`orchard cobalt the`.

Both natural and Boolean modes produce:

| Point | `orchard` matches | `cobalt` matches | `the` matches |
|---|---|---|---|
| Initial index | none | 2 | 3 |
| First restart, before writes | none | 2 | 3 |
| Insert after source changed | 5 | 2 | 3,5 |
| Second restart, source missing | 5 | 2 | 3,5 |
| Insert after source dropped | 5,6 | 2,6 | 3,5 |

The custom variables themselves return NULL after restart. The index remembers
its source independently, reloads that source for future writes, and falls back
to built-in filtering if the source is gone. Old postings are not rebuilt. A
newly excluded word such as `cobalt` remains searchable in old postings.
A source missing when an index is first built is not remembered: recreating
that table before restart leaves built-in filtering active.

## Storage requirements

An index stores its remembered source separately from its active write policy
and each document's historical rules. Session selection is resolved lazily once
when a statement needs the list, so index construction and its WAL event capture
the same words. Metadata index rename retains the source; a physical rebuild
captures the selected settings again. Recovery restores historical postings,
then reloads remembered sources for future writes. A missing remembered source
selects built-in filtering without rebuilding old postings.

WAL tag `0x1D` captures the source and loaded policy around DDL. It composes
with the existing ngram and disabled-filtering contexts. Public commit observers
and temporary-table filtering unwrap these contexts.

WAL recovery must also distinguish writes before and after a source reload.
WAL tag `0x1C` wraps insert/update batches and ALTER events with each full-text
index's loaded stopword policy. Replay selects those active policies before
applying the mutation;
existing document rules retain their own historical lists. The wrapper composes
with ngram-size context and transaction envelopes. Temporary-table filtering and
commit observers unwrap it consistently. This records a return to built-in
filtering as well as custom-list changes, so replay does not depend on whichever
list was loaded by an earlier DDL event.
Query lookup must keep old postings reachable after the write policy changes,
while phrase handling still observes stopword semantics.

The native restart oracle also adds a second index after changing the source
from `orchard` to `cobalt`. Querying the original index first loads the new list:
the added index excludes `cobalt` and includes `orchard` and `the`. Fsdb matches
this path, including a further WAL recovery before any ordinary row write.
When ADD FULLTEXT is the first access to a cold table after restart, MySQL instead
includes all three terms in the new index. A subsequent insert excludes `cobalt`.
Fsdb eagerly reloads sources during recovery, so its added index excludes
`cobalt` even on this cold path. This lazy-loading distinction remains a gap.

Additional ALTER variants, query expansion, and more
source charset/collation combinations remain outside this verified matrix.

## Indexing-rule foundation

`FullText.IndexingRules` groups the tokenizer and stopword policy. An index keeps
its active rules separately from the rules captured by each document.
Reconstruction accepts historical rules per document, and transaction publication
and metadata ALTER carry those complete rules rather than only token sizes.
Physical rebuilds still apply the selected rules to every document.

The core regressions cover mixed ngram sizes and stopword policies, active-rule
changes, reconstruction, prefix removal, transaction merging, and metadata rename
versus physical rebuild. [Ordinary word lookups](2026-10-07-fulltext-word-stopword-postings.md)
also preserve historical postings when active filtering changes, including after
snapshot recovery. Custom lists capture literal words and their source collation;
compiled keys filter words and ngram substrings. Empty lists
replace the defaults, and phrases use custom words for anchor selection.
The SQL lifecycle regression covers both WAL-only and checkpointed histories,
source edits, source removal, index rename, and a further restart after fallback.

## Snapshot history

Format 17 (`FSNH`) stores a deduplicated rule table for each full-text index,
followed by a length-encoded rule reference for each document. Rule entries pair
the tokenizer with the stopword policy. Active policies remain in the index
metadata alongside the remembered source, including for empty indexes; startup token-size selection keeps its
existing behavior independently of historical document tokenizers.

Custom policy entries store the source collation name and literal word list;
recovery reconstructs collation keys. Recovery preserves mixed ngram sizes and
built-in, disabled, or custom policies within one index. Rule counts are bounded by the row count plus one active rule; missing
or out-of-range document references are rejected. Regressions cover mixed
histories, subsequent writes, and malformed references with a valid checksum.
Real format-13 through format-16 fixtures verify backward reading. Earlier
formats supply no remembered source. Older fsdb binaries cannot read `FSNH`
or replay WAL tags `0x1C` and `0x1D`.

## Verification

The complete native oracle passes, including the same-datadir restart sequence
and searches in natural and Boolean modes after each transition.

The indexing-rule and snapshot changes pass `just check` with 2,918 tests and no build
warnings or errors. The existing stopword configuration matrix passes on fsdb.
All 47 MySQL contracts (5,007 steps) pass without differences:
`torture/artifacts/runs/20261007T081648185-5907/contracts`.

The durability lane passes 12 crash restarts, preserving all 106 acknowledged
commits and transaction boundaries, with checkpoint, WAL-tail, snapshot, schema,
and torn-tail checks:
`torture/artifacts/runs/20261007T081658373-5963/durability-seed101-workers4-ops100-restarts8-checkpoint16`.

The SQL wire matrix agrees for InnoDB source validation, precedence, loaded-list
lifetime, physical rebuild, collation, phrases, and ngram filtering. No known-gap
suppression is added.
