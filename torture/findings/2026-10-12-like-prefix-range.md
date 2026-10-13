# Indexed binary-collated LIKE prefixes

Native MySQL 8.4.11 uses `range` access on indexed `VARCHAR(20)` columns
with `utf8mb4_0900_bin` or `utf8mb4_bin` for `LIKE 'ab%'`,
`LIKE 'é%'`, and `LIKE '😀%'`. The 0900 collation also uses a range for
`LIKE 'ab_%'`. It also uses a prefix-length index on `TEXT`, `TINYTEXT`,
`MEDIUMTEXT`, and `LONGTEXT` for `LIKE 'abc%'`. Indexed `CHAR(20)` under
either binary collation also uses a range for fixed prefixes. The ASCII probes return the
same rows as a table without the index:
`ab` matches only the first pattern, while `abc`, `abZ`, `ab `, and
`ab%` match both. A leading wildcard cannot supply a lower bound.

Fsdb now derives an ordered-key interval from the fixed UTF-8 portion
before the first unescaped wildcard. The query still evaluates its full
`LIKE` predicate on candidates, so `_` and later pattern characters retain
their normal meaning. The upper bound advances the last Unicode scalar with
room for a successor, skipping the surrogate range; an all-maximum prefix has
only a lower bound. Fsdb's `utf8mb4_bin` ordered index sorts longer
trailing-space variants before the bare prefix, so its lower bound uses the
preceding significant scalar.
The residual `LIKE` test distinguishes `ab` from `ab ` even though indexed
comparison pads spaces. The planner chooses the range only when its observed
candidate count beats a scan. This path covers literal patterns with default
backslash or explicit `ESCAPE` semantics, including escaped wildcard characters
and exact patterns. Safe constant `CONCAT`, `IF`, and `COALESCE` patterns use the
same interval on binary-collated `CHAR`, `VARCHAR`, and indexed `TEXT` family keys.
An explicit pattern `COLLATE` can use the range when every override names the
column's binary collation. MySQL uses `range` for an explicit
`utf8mb4_0900_bin` pattern on a `utf8mb4_0900_bin` column, including a
constant `CONCAT` pattern. A different collation excludes the range: with
`utf8mb4_0900_ai_ci`, `ABX` also matches `ab%` despite the column's binary
collation. The query falls back to a scan and still returns that row. MySQL
also scans for a `utf8mb4_bin` override on a `utf8mb4_0900_bin` column.
For byte-valued `BINARY`, `VARBINARY`, and indexed `BLOB` family keys, the
range instead uses a byte prefix and its next lexicographic byte string.
An all-`FF` prefix has no upper bound. The residual predicate compares raw
bytes: connection text is encoded in its source charset, while introduced
charset values retain their declared bytes. Thus a UTF-8 `é%` matches
`0xC3A9`, while `_latin1 0xE925` matches `0xE9`. Prefix-length `BLOB`
indexes widen the range before the residual filter.
For a prefix-length key such as `v(2)`, storage widens the interval to
include every row sharing the truncated key; the residual `LIKE` test
selects `abc` from an `abc%` probe. Other
collations, variable-dependent expression patterns, and composite-key prefixes
remain open.

The focused Expecto tests first reported `ALL` where MySQL chose `range`.
They now check both ASCII prefix patterns, BMP and supplementary Unicode
prefixes, `TEXT` family keys, escaped `_` and `%`, an explicit escape
character, an exact pattern, safe constant expression patterns, matching and
different explicit collation overrides, byte-valued prefixes and source encodings,
trailing-space variants under the legacy binary collation, result parity with a scan, the
leading-wildcard control, and the truncated-index candidate range. The `CHAR`
probes check that default trailing-space stripping leaves only `ab c` matching
`LIKE 'ab %'`. The `binary-like-prefix-range` differential contract compares
the returned rows with native MySQL.

All forty contract steps matched the digest-pinned Docker MySQL 8.4.11
run (`20261013T204333928-64383/contracts`), and the case restored its database
objects. The full 150-case, 19,132-step run had ten differences outside this
case, including identifier-case diagnostics and the connection-sensitive
mixed-type join. An earlier native Homebrew run reported a Han REGEXP property
difference because that build ships different Unicode property data; the Han
case matches the pinned image.

The [focused quick benchmark](../../benchmarks/results/2cf93f5d-quick.md)
used 10,000 `functional_users` rows and the same `prefix42%` query with
the key enabled or ignored. Fsdb averaged 691 µs indexed versus 21.1 ms
scanning (about 30.5× faster); MySQL averaged 85 µs indexed versus
1.64 ms scanning (about 19.3× faster). Fsdb's indexed read remained about
8.1× slower than MySQL's. These are three-iteration ShortRun measurements
on one host, not a broader throughput or durability claim.
