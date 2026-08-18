# Compatibility

How fsdb's MySQL compatibility is validated, and the current evidence.

## Validation method

fsdb is validated by migrating and running the test suites of real Laravel
applications against it, unmodified. Where a suite diverges from its sqlite
baseline, the dispute is settled by running the same tests against a real
MySQL 8.4 — fsdb must match MySQL, not sqlite.

`torture/` adds a differential fuzz harness: generated SQL runs against both
fsdb and a MySQL 8.4 oracle, and the first divergence is classified and
replayable (`torture/scripts/run.sh suite`; exit 0 = pass/known gaps, 2 = new
fsdb findings).

## Gauntlet

| Application | Laravel | Migrations | Result |
|---|---|---|---|
| App A | 11 | 94 | full parity, 0 failures |
| App B | 11 | 205 | parity; 5 residual failures reproduce identically on real MySQL (app-side factory/collation bugs) |
| App C | 10 | 160 | behavioral equivalence with real MySQL (identical failure set from an app-side factory bug) |
| App D | 13 | 43 | parity; 1 residual failure is a sqlite-only PRAGMA introspection test that fails identically on real MySQL |
| App E | 13 | 487 | full 10,972-test suite: 10,913 passed, all 36 failures individually verified as app-side bugs or real-MySQL-identical; one documented order divergence on an unordered query |

The applications are private codebases, identified here only by framework
version and size.

## Milestones

All ten roadmap milestones are done — wire protocol, PDO/mysql-CLI
compatibility, the SQL engine core, Laravel migrations, test-suite parity,
the embedding API, opt-in persistence, EXPLAIN + multi-table DML,
performance-without-ugliness, and the streaming pipeline. See
[ROADMAP.md](ROADMAP.md) for the plan, acceptance gates, and per-milestone
evidence.

## Users, authentication, and privileges

fsdb has a real account system backed by a stored `mysql` schema (`user`,
`db`, `tables_priv`, `columns_priv`, `global_grants` — MySQL 8.4 column
shapes, oracle-verified). `CREATE USER` / `DROP USER` / `ALTER USER` /
`SET PASSWORD` / `GRANT` / `REVOKE` persist through the ordinary WAL/snapshot
path; passwords are mysql_native_password hashes verified at the handshake
(with an AuthSwitchRequest for clients that answer with caching_sha2 first);
statements are privilege-checked at global, database, and table scope with
MySQL's 1045/1142/1044/1227 error shapes. `SHOW GRANTS [FOR user]`,
`SHOW PRIVILEGES` (8.4's 73 rows), `information_schema.USER_PRIVILEGES`, and
`FLUSH PRIVILEGES` (no-op OK) are served; `DROP DATABASE mysql` is 3552.

Deliberate divergences (each marked `ponytail:` at its code site):

- One host per account, matched by name only; the connecting host always
  renders as `localhost`, accounts default to `'%'`.
- An account with an **empty** `authentication_string` accepts any offered
  password (real MySQL would reject a wrong one) — keeps passwordless setups
  and the torture harness's `root`/`torture-secret` connection working.
- Enforcement covers parsed statements' top-level table references;
  SHOW/SET text probes and subqueries/derived tables are unchecked.
- No roles, dynamic privileges, column-level privileges, proxy users, or
  password expiry — the columns exist in the table shapes but stay at their
  defaults. 5 of MySQL's 38 `mysql.*` tables exist.
- `SHOW GRANTS` omits root's dynamic-privilege and PROXY lines.
