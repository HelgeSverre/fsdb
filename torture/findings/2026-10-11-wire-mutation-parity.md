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

This is evidence for this fixed corpus and seed, not proof of complete wire
compatibility. Broader mutation counts and other command families remain open.
