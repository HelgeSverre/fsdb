# CRC32 functional indexes

MySQL 8.4.11 accepts `KEY ix_crc ((CRC32(v)))` and uses it for `CRC32(v)=CRC32('abc')`: `EXPLAIN` reports `ref` access through `ix_crc`. Fsdb previously kept the declaration but scanned the predicate.

The physical transform now calculates the same CRC-32 as scalar `CRC32()`, using raw bytes for binary and encoded values and the source character set for stored text. It classifies the result as an exact signed integer, so equality and range probes, uniqueness, order, mutation maintenance, and composed numeric keys use the existing index pipeline. Constant `CRC32(...)` bounds are folded only when the built-in has not been overridden.

The `crc32-functional-index` MySQL differential contract covers text, binary, integer, latin1, NULL, uniqueness, and update maintenance. A focused Expecto regression also asserts physical access and override safety. Other unsupported expressions remain in the broader expression-index gap.
