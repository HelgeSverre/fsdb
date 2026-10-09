# YEAR column values and foreign-key type families

MySQL 8.4.11 stores numeric YEAR inputs 1–69 as 2001–2069 and 70–99 as
1970–1999. Numeric zero and the string `'0000'` store zero, while the
all-zero strings other than `'0000'` store 2000. A numeric prefix followed by
text, such as `'24x'`, raises 1265 in strict mode; non-strict mode stores
2024 with warning 1265. The accepted four-digit range is 1901–2155; 100
and 1900 return 1264 under the default
strict mode. The `year-column-values` differential contract checks the
representative values, strict errors, unchanged row count, non-strict stored
values, and warning row ordinals. fsdb now normalizes
these inputs at column coercion, so stored rows and indexes use the same
canonical year.

Further creation probes found that MySQL accepts foreign keys pairing YEAR
with `TINYINT UNSIGNED` in either direction. Its referential comparison uses
the YEAR storage byte: YEAR 2024 matches tinyint 124, and YEAR 1924 matches
tinyint 24. Update cascades map 2024 to 2025 and tinyint 124 to 125 in either
direction; zero also matches zero. The `year-byte-foreign-keys` contract
covers declarations, parent lookups, updates, deletes, and zero keys in both
directions, plus rejection of signed tinyint. fsdb uses a shared physical-key
rule across parent probes, insert lookups, cascades, and transaction validation.

MySQL also accepts TIME paired with DATETIME or TIMESTAMP in either direction,
including differing fractional precisions. DATE remains incompatible with
them. Ordinary matching-clock-field inserts and zero-value inserts in the
native probe returned 1452. The `time-date-foreign-keys` contract covers
declaration and nonmatching ordinary inserts in both directions; fsdb now
accepts those declarations and keeps their referential comparisons distinct.
Other physical-key combinations and referential actions remain unverified.
