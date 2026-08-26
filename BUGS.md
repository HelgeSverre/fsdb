# Known bugs

## External MySQL compatibility

The SQL examples below succeed on MySQL 8.4.

### Session SQL mode does not enable ANSI identifier quotes

Status: open

Drupal's MySQL driver executes `SET sql_mode = 'ANSI,TRADITIONAL'` and then
quotes identifiers with double quotes. fsdb acknowledges the session command
but parses those identifiers as string values. Drupal's installer consequently
fails on statements such as:

```sql
CREATE TABLE "test14800862drupal_install_test" (id int NOT NULL PRIMARY KEY)
```

Reproduce with `just smoke-apps drupal`. The pinned Drupal
`ConnectionTest::testMultipleStatementsForNewPhp` fails before the schema test
can run.

### XORM schema creation and introspection do not parse

Status: open

Gitea connects and creates its test database, but its XORM migration cannot
create the `user` table. The generated definition combines type display widths,
inline `PRIMARY KEY AUTO_INCREMENT`, boolean defaults, negative defaults, and
inline indexes. After that failure, XORM's `INFORMATION_SCHEMA.COLUMNS`
introspection query also fails to parse; it uses `&&`, `INSTR`,
`SUBSTRING_INDEX`, `VERSION`, and a qualified `ORDER BY` expression.

Gitea's preceding database-collation adjustment is rejected separately:

```sql
ALTER DATABASE CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs
```

Reproduce with `just smoke-apps gitea`. The pinned `TestUser` integration test
reaches ORM initialization and fails before fixtures load.

### MediaWiki's generated MySQL tables do not parse

Status: open

MediaWiki connects, creates its database, and begins loading its generated
MySQL schema. fsdb rejects the first table, which combines unsigned columns,
inline named unique indexes, an auto-increment primary key, and table options:

```sql
CREATE TABLE `actor` (
  actor_id BIGINT UNSIGNED AUTO_INCREMENT NOT NULL,
  actor_user INT UNSIGNED DEFAULT NULL,
  actor_name VARBINARY(255) NOT NULL,
  UNIQUE INDEX actor_user (actor_user),
  UNIQUE INDEX actor_name (actor_name),
  PRIMARY KEY(actor_id)
) ENGINE=InnoDB, DEFAULT CHARSET=binary
```

Reproduce with `just smoke-apps mediawiki`. Installation fails before
`DatabaseIntegrationTest` can run.

### Nextcloud's Doctrine migration tables do not parse

Status: open

Nextcloud connects and enters its Doctrine DBAL migration sequence. fsdb
rejects the first lock table, which combines unsigned auto-increment columns,
a negative default, quoted reserved-word identifiers, inline indexes, and
Doctrine's table-option form:

```sql
CREATE TABLE oc_file_locks (
  id BIGINT UNSIGNED AUTO_INCREMENT NOT NULL,
  `lock` INT DEFAULT 0 NOT NULL,
  `key` VARCHAR(64) NOT NULL,
  ttl INT DEFAULT -1 NOT NULL,
  UNIQUE INDEX lock_key_index (`key`),
  INDEX lock_ttl_index (ttl),
  PRIMARY KEY(id)
) DEFAULT CHARACTER SET UTF8 COLLATE `utf8_bin` ENGINE = InnoDB
```

Reproduce with `just smoke-apps nextcloud`. Installation fails before the
DB-tagged PHPUnit tests can run.

### The default storage engine session variable is missing

Status: open

Shopware connects, creates its test database, imports its base schema, and
starts its 874 core migrations. The migration runtime stops before the first
migration because fsdb returns error 1193 for this valid MySQL session setting:

```sql
SET default_storage_engine=InnoDB
```

Reproduce with `just smoke-apps shopware`. The bootstrap reports zero completed
migrations and exits before the test suite can run.
