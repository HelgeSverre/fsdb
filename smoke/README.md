# External application smoke tests

These probes run unmodified, pinned upstream projects against a fresh fsdb
process. Every target gets an isolated Docker network and database server, so a
failed installer cannot affect another target.

The ten applications add compatibility evidence that the Laravel application
gauntlet does not provide:

| Target | Client stack | Quick gate |
|---|---|---|
| Gitea | Go MySQL driver and XORM | Current schema, fixtures, and one integration test |
| MediaWiki | PHP mysqli and MediaWiki RDBMS | Full install and `DatabaseIntegrationTest` |
| Drupal | PHP PDO and Drupal database API | All upstream component, unit, kernel, functional, JavaScript-functional, and build tests |
| Nextcloud | PHP PDO and Doctrine DBAL | Full install and DB-tagged PHPUnit tests |
| Shopware | PHP PDO and Doctrine DBAL | Full migration-driven test database install |
| Ghost | Node.js MySQL driver and Knex | Schema bootstrap and deep member pagination test |
| Moodle | PHP mysqli and Moodle DML | PHPUnit bootstrap plus core DDL and DML suites |
| WordPress | PHP mysqli and wpdb | Database-focused core PHPUnit tests |
| Rails | Ruby mysql2 and Active Record | Full test schema plus a MySQL adapter test |
| Magento | PHP PDO and Magento DB adapter | Full application install with OpenSearch |

The pinned Nextcloud suite currently has one order-dependent upstream failure:
`TrashbinTest` retains `test-trashbin-user2`, which changes the following
recipient-search expectation. The same two-class sequence fails against MySQL
8.4; it is not an fsdb compatibility difference.

The upstream commits live in `versions.env`. Updating a pin is a deliberate
compatibility-corpus change: run that target against MySQL 8.4 as well as fsdb
before classifying new failures.

## Run

The runner uses the repository's .NET toolchain and Docker; the `just` recipes
also require `just`. The first run builds language runtimes and downloads
upstream dependencies, so it is much slower than later cached runs.

Application images use separate build and runtime stages. Gitea retains its Go
module tree because its integration gate compiles the selected test, and Ghost
retains workspace development dependencies because the gate runs through
Vitest.

```sh
just smoke-apps gitea
just smoke-apps mediawiki drupal
just smoke-apps ghost moodle wordpress rails magento
just smoke-apps
```

Build images without running a probe:

```sh
just smoke-apps-build gitea
```

Reuse already-built images:

```sh
dotnet fsi --nologo --readline- --exec smoke/run.fsx -- --no-build gitea
```

Each run writes the upstream output and the corresponding fsdb server log under
the ignored `smoke/results/<UTC timestamp>-<process ID>/` directory. The command
continues through all selected targets and exits nonzero when any target fails.

Reproducible fsdb failures found by these probes become focused regression
tests. Deliberate compatibility boundaries remain inventoried in `GAPS.md`.

Most targets remain focused compatibility gates. Drupal is the exception: its
target runs every upstream PHPUnit suite at the pinned revision,
including tests that do not access a database. Drupal's own test runner uses
eight workers by default. The PHP development server uses twice as many workers
so nested requests cannot exhaust the request pool. Set `DRUPAL_CONCURRENCY` or
`DRUPAL_HTTP_WORKERS` to tune either pool locally. Its per-class JUnit reports,
runner database, HTTP log, and WebDriver log are kept with the campaign
results. A failure is a candidate fsdb compatibility only when the same pinned
test
succeeds against MySQL 8.4; environment and upstream failures remain separate
classifications.

Set `DRUPAL_TEST_CLASSES` to a comma-separated list of fully qualified class
names to resume or isolate part of the Drupal inventory.
