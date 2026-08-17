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
