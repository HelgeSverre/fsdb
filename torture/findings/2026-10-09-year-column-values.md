# YEAR column values and foreign-key type families

MySQL 8.4.11 stores numeric YEAR inputs 1–69 as 2001–2069 and 70–99 as
1970–1999. Numeric zero and the string `'0000'` store zero, while the
one- and two-character strings `'0'` and `'00'` store 2000. The accepted
four-digit range is 1901–2155; 100 and 1900 return 1264 under the default
strict mode. The `year-column-values` differential contract checks the
representative values, error, and unchanged row count. fsdb now normalizes
these inputs at column coercion, so stored rows and indexes use the same
canonical year.

Further creation probes found that MySQL accepts foreign keys pairing YEAR
with `TINYINT UNSIGNED` in either direction. Its referential comparison uses
the YEAR storage byte: YEAR 2024 matches tinyint 124, and YEAR 1924 matches
tinyint 24. Update cascades map 2024 to 2025 and tinyint 124 to 125 in either
direction; zero also matches zero. fsdb currently rejects this pair with 3780.
Admitting the DDL requires a shared physical-key rule across parent probes,
insert lookups, cascades, and transaction validation.

MySQL also accepts TIME paired with DATETIME or TIMESTAMP in either direction,
including differing fractional precisions. DATE remains incompatible with
them. Ordinary cross-family row inserts in the probe returned 1452; fsdb
currently rejects these definitions with 3780. Their physical-key and
referential-action behavior remains unverified, so these declarations remain
an open compatibility gap.
