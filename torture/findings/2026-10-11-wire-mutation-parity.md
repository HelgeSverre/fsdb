# Wire mutation parity, 11 October 2026

The wire torture lane compares raw command packets and connector options against
the pinned MySQL 8.4 server and fsdb. This run used seed 1, 32 generated
mutations, and 38 baseline/capability cases: 70 cases in total. The passing
manifest is in
`torture/artifacts/runs/wire-2026-10-11/20261011T000557108-76112/wire`.
That artifact is local and ignored by Git; the command to reproduce it is:

```sh
bash torture/scripts/run.sh wire --wire-cases 32
```

The first run could not set up its MySQL fixture because its generated account
name was 42 characters, over MySQL's 32-character account-name limit. The
fixture now uses a shorter random suffix. TLS connector cases also inherited
an inaccessible default database; selecting `information_schema` makes the
connection test the intended TLS/compression behavior on both targets.

With those fixture issues removed, the corpus exposed two fsdb differences.
`COM_FIELD_LIST` without a NUL separator returned table-not-found; MySQL
returns malformed-packet error 1835 (`HY000`). A restricted account selecting
an absent database during handshake or `COM_INIT_DB` received unknown-database
1049 from fsdb; MySQL checks privileges first and returns 1044 (`42000`).
The command parser, database-selection check, and shared 1044 SQLSTATE mapping
now match these observations. The focused integration and protocol assertions
cover them. The final wire run passed all 70 cases with zero differences.

This first run was evidence for one fixed corpus and seed, not proof of
complete wire compatibility.

The corpus was then widened to 128 mutations on each of seeds 1 and 2. Seed 2
found that `/* comment */;;#` returned OK on fsdb while MySQL returned syntax
error 1064 (`42000`). MySQL accepts a trailing delimiter after a comment, but
rejects another comment after that delimiter when no statement exists. The
empty-statement wire path now distinguishes these forms; a focused raw-packet
regression covers line and block comments after delimiters. Both widened runs
now pass all 166 cases with zero differences:

```sh
bash torture/scripts/run.sh wire --seed 1 --wire-cases 128
bash torture/scripts/run.sh wire --seed 2 --wire-cases 128
```

Passing manifests are under local ignored artifact runs
`20261011T001127266-77576/wire` and `20261011T001112193-77520/wire`,
respectively. Other seeds and protocol families remain unproven.
