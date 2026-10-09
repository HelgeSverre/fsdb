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
including differing fractional precisions, and DATETIME paired with TIMESTAMP
in either direction. DATE remains incompatible with them. Ordinary
matching-clock-field inserts and zero-value inserts in the native TIME probes
returned 1452; equal displayed DATETIME/TIMESTAMP values and zero values
under `NO_ENGINE_SUBSTITUTION` also returned 1452.
The `time-date-foreign-keys` contract covers declaration and nonmatching
ordinary and zero-value inserts in both directions for these pairs. fsdb
accepts those declarations and compares their distinct physical key domains
rather than equating zero date-time values across DATETIME and TIMESTAMP.
Other physical-key combinations and referential actions remain unverified.

Within each of `TIME`, `DATETIME`, and `TIMESTAMP`, MySQL 8.4.11 accepts
foreign keys with different fractional precisions but matches values by the
fractional storage-byte tier: precision 0 uses no extra bytes, 1–2 use one,
3–4 use two, and 5–6 use three. Equal displayed values match within a tier
(`TIME(1)` to `TIME(2)`, for example) and fail with 1452 across tiers
(`TIME(2)` to `TIME(3)`), in either precision direction. fsdb now uses that
same physical-key boundary for all three temporal families.
The expanded `time-date-foreign-keys` contract passed all 148 steps at
`torture/artifacts/runs/20261009T233133629-60515/contracts`; `just check`
passed all 3,250 tests. The full contract run retained only the nine
identifier-case differences from the pinned server's case mode.
