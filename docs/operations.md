# Preview operations

FSDB's WAL-backed mode is opt-in (`fsdb --data-dir PATH`). The data directory
contains the catalog, user accounts, WAL, and snapshots. Treat it as a single
unit, restrict write access to the FSDB process account, and keep a separate
copy before upgrades. The preview format has recovery tests, but no supported
online backup or cross-version migration guarantee yet.

## Back up and restore

For a physical backup, stop the listener and all embedded connections cleanly,
then copy the **whole** data directory while FSDB is stopped. Keep
`snapshot.fsdb`, any `snapshot.fsdb.new`, and `wal.bin` together. Copying only
the snapshot can lose later committed writes; copying live files at different
moments can produce an inconsistent pair. `Db.own` checkpoints on disposal,
after it closes its owned connections. A server process should be stopped
before copying its directory.

Restore into a new, empty directory owned by the FSDB process account. Start
FSDB against that directory on loopback, inspect recovery diagnostics, and
query representative schema, row counts, and account grants. Test the restore
regularly with a copy of production data before relying on the backup.
The original directory should remain untouched until the restored copy has
been checked.

## Upgrade a durable directory

Record the current FSDB version and back up the stopped data directory. Copy
it to a staging directory, then start the proposed version against that copy.
Run application reads and writes, stop it cleanly, and reopen it with the same
version to exercise replay and checkpoint loading. Promote only after those
checks succeed. Do not open the original directory with a new preview version
until the staging restore has passed; downgrade compatibility is not promised.

## Performance gate

After changing write publication, WAL, or lock scheduling, run
`just bench-write-gate` twice on the same quiet host. It runs the durable
latency and worker-scaling suites together.
The first compares WAL-backed FSDB with durable MySQL; the second captures
throughput at 1, 2, 4, 8, and 16 workers. Keep the generated benchmark reports
with their revision, engine versions, modes, and dataset sizes. Investigate a
regression against the previous same-host run before accepting it; the in-memory
FSDB write numbers from other recipes are not durable-write evidence.
The disposable MySQL server keeps InnoDB's commit-time flush enabled but disables
binary logging; replication logging is outside this single-node durability comparison.
