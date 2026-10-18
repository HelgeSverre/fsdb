# Laravel application compatibility probe (2026-10-16)

This probe ran existing application migrations and feature tests against a
disposable, in-memory fsdb server on port 33317. Each application had its own
database. The application checkouts were not changed. Test commands supplied
MySQL connection settings through the process environment, with array cache,
array sessions, and a synchronous queue. Reflow's test command disabled code
coverage and raised PHP's memory limit because its coverage analyser exhausted
the default 128 MiB before producing a database result.
The tested heads were Reflow `f93e5bae`, Boatflow `d53bbb53`, Chatflow
`8b1effa4`, Spamcalls `57fc2378`, crescat-website `d1e9acf1`, and Sysflow
`6a94c448`. PHP 8.5 ran the first four except Boatflow; Boatflow and Sysflow
used PHP 8.4.

| Application | Migration result | Existing feature-test result on fsdb |
|---|---|---|
| Reflow | All migrations completed | Customer contacts: 6 active tests passed; invoice splitting: 13 passed on a sequential retry |
| Chatflow | All migrations completed | Chatbot creation/validation: 3 active tests passed and 1 skipped; daily event summaries: 8 passed |
| Spamcalls | All migrations completed | Country listings: 10 passed, 93 assertions; weekly summaries: 5 passed, 12 assertions |
| Sysflow | Test harness migrated its schema | Search: 2 passed; category pages: 6 passed |
| Boatflow | All migrations completed | Company default statuses: 6 passed; company settings: 4 passed, using a temporary PHPUnit config that selected the fsdb MySQL database |
| crescat-website | Stopped at `2026_09_23_185837_add_open_request_unique_index_to_venue_claims_table` | The migration uses `CREATE UNIQUE INDEX ... WHERE ...`. The pinned MySQL 8.4.11 image rejects that partial-index syntax with 1064/42000 too; this is an application MySQL-portability issue, not an fsdb divergence. |

The first Spamcalls country-listing run crashed fsdb during a seeder's
200-row prepared bulk insert. `PreparedMetadata.bindParameters` built one
call frame per marker through `Result.bind`; the process reached a stack
overflow. An iterative binder now handles a 3,000-marker regression, and the
same Spamcalls feature file passes after the fix. The pinned MySQL 8.4.11
control also passes that file's 10 tests and 93 assertions. Its single-run
duration was 3.1 seconds versus 9.1 seconds on fsdb; migration, seeding, and
test setup are included, so this is a workload observation rather than an
isolated query benchmark.

An overlapping run of Reflow invoice, Chatflow reporting, and Spamcalls
summary files on one fsdb process caused Reflow's first test to time out after
50 seconds while reading `information_schema.tables` (1205). The other two
suites completed after roughly 51–54 seconds. A fresh `SELECT 1` also waited
until that interval ended. Reflow's invoice file then passed all 13 tests in
9.3 seconds when rerun alone. The process remained alive. This observation
prompted an isolated multi-database reproduction before attributing a
scheduler defect or estimating concurrent application throughput.

Follow-up on the same three-file overlap reproduced the problem: Reflow timed
out on `RENAME TABLE permissions TO old_permissions` after 62.5 seconds,
Chatflow timed out on `CREATE TABLE chatbot_access` after 52.9 seconds, and
Spamcalls passed but took 54.8 seconds. A managed dump taken during the wait
showed two DDL requests blocked in `Storage.withDatabaseLocks`, another request
blocked publishing a transaction in `Storage.commitCatalogIntoWith`, and a
fourth blocked in `Storage.bumpAutoIncrementsInto`. These waits involve database
slot monitors rather than a crashed process. A simple control with one open
transaction in database A and an `information_schema.tables` read plus `SELECT
1` in database B returned in 35 ms and 2 ms, respectively. Thus an open
cross-database transaction alone is insufficient to reproduce the problem.

The likely contention amplifier is `catalogHasQualifiedForeignKeys`: when any
snapshot contains a schema-qualified foreign key, `commitCatalogIntoWith`
sets `validateWholeSnapshot` and locks every database slot in the snapshot,
even when the transaction changed only one database. Concurrent DDL takes the
`mysql` metadata slot plus its target database slot. With several Laravel
migration/test processes, these paths can therefore contend across otherwise
independent databases. The dump establishes the wait sites, but does not prove
a specific lock-order cycle or identify which qualified foreign key triggered
whole-catalog validation. The focused regression below tests the locking
scope without depending on that exact cycle.

A follow-up focused regression held the parent database slot of a qualified
foreign key while inserting and committing in an unrelated third database.
Before the change, both unrelated write paths waited for that slot. MySQL
8.4.11 completed the corresponding unrelated insert in 7 ms while the parent
table was locked. fsdb now limits qualified-foreign-key whole-catalog
validation to writes involving the child or parent database and skips
unchanged database slots when publishing transaction AUTO_INCREMENT advances.
The focused regression also confirms that a child write still waits while the
parent database slot is held, while unrelated writes and commits finish. The
full 3,707-test gate passes. With fresh isolated
databases, the same three application files overlapped successfully: Reflow
13 tests in 12.9 seconds, Chatflow 8 in 1.9 seconds, and Spamcalls 5 in 4.3
seconds. These file durations overlap and are not an aggregate throughput
benchmark. Broader concurrent workloads and cross-database foreign-key pairs
still need dedicated scheduling measurements.

The crescat-website migration is SQLite-specific: its `WHERE status =
'pending' AND email_verified_at IS NULL` clause makes a partial unique index,
which MySQL 8.4 rejects. Its rollback also uses `DROP INDEX IF EXISTS`, another
SQLite-style form. For MySQL support, the application should use a MySQL-valid
conditional generated key (NULL outside open requests) with a unique index, or
maintain separate database-specific migration paths. fsdb should continue to
reject the partial-index syntax for MySQL compatibility.

These are focused smoke tests, not full application suites or browser flows.
Boatflow's checkout initially lacked its locked Bugsnag packages; installing
the lockfile under PHP 8.4 restored them. Its ignored Laravel package and
service manifests still named an uninstalled Sentry provider; deleting those
stale generated caches let Laravel rebuild them and boot. The temporary
PHPUnit config was removed after the tests. These establish working migration
and representative Eloquent/Livewire paths for the four named runnable apps,
and a narrower test-harness path for Sysflow. They do not establish production
suitability for any application.
