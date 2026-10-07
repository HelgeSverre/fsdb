# Full-text transaction visibility

Status: open. Native MySQL 8.4.11 and fsdb revision `7a8a5528` disagree on
pending writes and superseded full-text documents in repeatable-read snapshots.
Both word and ngram indexes are affected.

The [native oracle](../scripts/fulltext-transaction-oracle.py) starts a disposable
socket-only MySQL server and checks membership, projected MATCH results, rollback,
savepoints, cross-index visibility, and two-connection isolation boundaries.
Run it with native `mysqld`, `mysql`, and `mysqladmin` on PATH:

```sh
python3 torture/scripts/fulltext-transaction-oracle.py
```

## Pending writes

Seed three rows: `(1, 'orchard red')`, `(2, 'orchard blue')`, and
`(3, 'cobalt green')`, with a FULLTEXT index on the text column. Each case starts
from this committed state and uses boolean MATCH queries.

| Transaction operation | Ordinary row IDs | MySQL orchard matches | MySQL cobalt matches | fsdb difference |
|---|---|---|---|---|
| Insert `(4, 'orchard gold')` | 1,2,3,4 | 1,2 | 3 | orchard also finds 4 |
| Change row 1 to `cobalt orange` | 1,2,3 | 2 | 3 | cobalt also finds 1 |
| Change row 1 to `orchard orange` | 1,2,3 | 2 | 3 | orchard also finds 1 |
| Assign row 1's body to itself | 1,2,3 | 1,2 | 3 | none observed |
| Delete row 1 | 2,3 | 2 | 3 | membership agrees; scores differ |

MySQL returns zero when projecting MATCH on the pending inserted or updated row,
even though ordinary predicates can retrieve that row. Commit makes the new text
searchable. Full rollback and rollback to a savepoint restore the old postings.
The same membership results hold for the ngram terms `生日` and `中文`.

Updating one full-text column also hides the row from a second full-text index
over another column until commit. An update to an unrelated, non-full-text column
does not hide the row. Document visibility therefore cannot be modeled as an
independent changed-row filter for each index.

This agrees with the documented commit-time processing of full-text inserts and
updates. [MySQL transaction handling](https://dev.mysql.com/doc/refman/8.4/en/innodb-fulltext-index.html#innodb-fulltext-index-transaction-handling)

## Concurrent writes

A reader first establishes a REPEATABLE READ snapshot of the seeded rows. A second
connection then commits a mutation:

| Other connection's mutation | Reader's ordinary IDs | Reader's orchard matches | Reader's cobalt matches |
|---|---|---|---|
| Insert row 4 containing orchard | 1,2,3 | 1,2 | 3 |
| Change row 1 to cobalt | 1,2,3 | 2 | 3 |
| Delete row 1 | 1,2,3 | 2 | 3 |

fsdb incorrectly retains row 1's orchard match in the update and delete cases.
MySQL combines snapshot row visibility with the lifetime of the committed
full-text document: retaining an ordinary historical row does not retain its
superseded full-text posting. READ COMMITTED sees the newly committed row set and
text; the tested cases already agree in fsdb.

At READ UNCOMMITTED, ordinary reads can see another connection's pending insert
or updated text. MySQL MATCH still excludes the pending full-text document.
The native oracle covers pending insert, update, and delete cases at this level.

## Relevance and implementation boundary

Filtering fsdb's current score map is insufficient. In one three-row word-index
probe, a pending fourth orchard row changed MySQL's existing orchard scores to
approximately 0.09061906 while the pending row's score remained zero. fsdb scored
all three orchard rows at approximately 0.01560969. InnoDB statistics varied in
exploratory runs, so these are diagnostic samples rather than stable numeric
fixtures; the maintained oracle asserts visibility and zero pending-row
projections. Numeric parity needs controlled validation alongside the read model.

`fullTextScoresForTable` currently reads `Table.FullTextIndexes` directly. Those
indexes already contain the private transaction's inserts and updated text.
`publishRowsWithDocumentTokenizers` maintains each index independently when its
indexed fields change. Ordinary snapshot/rebase behavior lives in QueryHandler.

The fix needs to distinguish the ordinary row snapshot, committed full-text
document identity across indexes, and the corpus used for relevance. The private
write indexes must remain complete for commit, XA publication, and durability.
Savepoint restoration and no-op/non-full-text updates must retain the correct
document identity. Predicates and projected MATCH expressions must use the same
read model, including MATCH used by writes and joined sources.

## Verification

The maintained native oracle passes on MySQL 8.4.11. Separate disposable-server
comparisons against fsdb's Debug executable reproduced the differences above.
`just check` passes all 2,871 existing tests with no build warnings or errors.
No runtime fix or known-gap suppression is included with this finding.
