# Text literal charset identity

Native oracle: MySQL 8.4.11, disposable same-host server, 64 MiB InnoDB
buffer pool and redo capacity. The
[executable oracle](../scripts/text-literal-charset-oracle.py) checks names,
values, error codes, and SQLSTATE. Each case uses a fresh client connection;
statements within a case share their session. The runner is shared with the
NAME_CONST oracle.

## Literal identity

| Expression or context | Charset | Collation | Coercibility |
|---|---|---|---:|
| Ordinary `'a'`, default connection | utf8mb4 | utf8mb4_0900_ai_ci | 4 |
| Ordinary `'a'`, connection latin1_bin | latin1 | latin1_bin | 4 |
| `_latin1'a'` | latin1 | latin1_swedish_ci | 4 |
| `_utf8mb4'a'`, including a utf8mb4_general_ci connection | utf8mb4 | utf8mb4_0900_ai_ci | 4 |
| `N'a'` or `_utf8'a'` | utf8mb3 | utf8mb3_general_ci | 4 |
| Ordinary `'a'`, binary connection | binary | binary | 4 |
| `_binary'a'`, including a latin1 connection | binary | binary | 4 |

An introducer uses the charset's default collation, independently of the
connection's selected collation. Adjacent strings retain the first introduced
literal's identity. CONCAT of two latin1 literals and NAME_CONST with a latin1
value retain latin1_swedish_ci. CAST AS CHAR CHARACTER SET latin1 likewise
reports latin1_swedish_ci.

COLLATE rejects a different charset with 1253/42000. Examples include plain
`'a' COLLATE 'binary'` in a utf8mb4 connection, `_latin1'a' COLLATE utf8mb4_bin`,
and `N'a' COLLATE utf8mb4_bin`. Compatible latin1 and utf8mb3 controls succeed.

## Preparation and stored definitions

Preparing `SELECT CHARSET("a"),COLLATION("a")` in a utf8mb4_general_ci
connection captures utf8mb4/utf8mb4_general_ci. Executing after SET NAMES latin1
retains those results.

A view created from `'a' AS v` in a latin1_bin connection retains latin1_bin
when queried from a utf8mb4 connection. The tested mergeable literal view also
retains coercibility 4. A stored function returning COLLATION('a') similarly
retains its creation connection's latin1_bin collation.

## Encoded values and protocol metadata

Introducers accept quoted text, hexadecimal bytes, and bit strings. The
following results distinguish byte identity from decoded character count:

| Literal | HEX | LENGTH | CHAR_LENGTH |
|---|---|---:|---:|
| `_latin1'é'` from a UTF-8 client | C3A9 | 2 | 2 |
| `_latin1 X'C3A9'` | C3A9 | 2 | 2 |
| `_utf8mb4 X'C3A9'` | C3A9 | 2 | 1 |

Separate native `mysql --column-type-info -vvv` probes show that protocol
metadata describes the result encoding. With default utf8mb4 results, plain,
latin1-introduced, and national ASCII literals all expose VAR_STRING,
collation 255, and length 4. With SET NAMES binary, the plain ASCII literal
exposes VAR_STRING, collation 63, length 1, and NOT_NULL/BINARY flags. These
metadata observations are exploratory; the maintained oracle checks SQL
observable identity and values, not raw protocol descriptors.

## Current fsdb boundary

At `6ba0dd27`, direct probes return utf8mb4/utf8mb4_0900_ai_ci for both
`_latin1'a'` and `N'a'`. `_latin1'a' COLLATE utf8mb4_bin` succeeds, and the
latin1-introduced hexadecimal case fails with 1064/42000. The prepared literal
changes to latin1/latin1_swedish_ci after SET NAMES latin1.

The parser decodes introduced strings into an ordinary `Lit(VString ...)`,
and ordinary string expressions retain no preparation-time collation. The
expression representation must retain literal encoding and collation before
binding can enforce these contracts. Persistence and SQL rendering must retain
that identity without reinterpreting already-decoded introducer bytes. Parser
cache keys must distinguish connection collations when parsing captures them.
The existing shared literal binding traversal can then validate the retained
charset; explicit COLLATE and binary-conversion nodes remain separate concepts.
