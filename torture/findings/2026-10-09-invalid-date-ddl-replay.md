# Invalid-date DDL replay

MySQL 8.4.11 retains `DATE DEFAULT '2023-02-31'` when the session enables
`ALLOW_INVALID_DATES`. It also retains an existing `2023-04-31` through a
compatible ALTER in that mode. A later non-strict ALTER without the mode
coerces the same value to `0000-00-00`. The native probe used MySQL 8.4.11 on
the local disposable server.

fsdb previously replayed CREATE TABLE and ALTER TABLE using a replay store
without the originating `ALLOW_INVALID_DATES` setting. A failing persistence
regression demonstrated that an accepted invalid default became
`0000-00-00` after WAL reload. DDL WAL events now carry that one setting.
Replay applies it only while processing the wrapped event, preserving both an
allowed invalid date and an ALTER that intentionally coerced one to zero.
The regression checks WAL reload and a subsequent snapshot reload.
`just check` passed 3,222 tests. The crash/restart durability lane passed at
`torture/artifacts/runs/20261009T172639378-3533/durability-seed101-workers16-ops500-restarts20-checkpoint16`,
including automatic checkpoints, WAL-tail recovery, schema recovery, and
torn-tail repair.

WAL records written before this event tag remain readable but do not record
the originating mode, so replay cannot distinguish those two historical
outcomes from the record alone. A snapshot created before upgrading retains
the already-materialized schema and rows.
