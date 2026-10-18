# Nüshu ngram audit

Native MySQL 8.4.11 and fsdb were compared over printable characters in
U+1B170–U+1B2FF using the maintained two-column
[`ngram-multicol-oracle.py`](../scripts/ngram-multicol-oracle.py). Each search
checks matching row IDs and relevance scores in natural-language, Boolean,
and query-expansion modes. The documents surround each character with CJK
text so the ngram key is exercised at the configured token size.

| Collation | Token sizes | Searches | Differences |
|---|---:|---:|---:|
| `utf8mb4_general_ci` | 1–3 | 7,128 | 0 |
| `utf8mb4_unicode_520_ci` | 1 | 2,376 | 0 |
| `utf8mb4_0900_ai_ci` | 1 | 2,376 | 0 |

The general-collation run was split at U+1B200 to keep each disposable
server's corpus bounded. These were the commands:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 1B170:1B200 --collation utf8mb4_general_ci 1 2 3
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 1B200:1B300 --collation utf8mb4_general_ci 1 2 3
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 1B170:1B300 --collation utf8mb4_unicode_520_ci 1
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 1B170:1B300 --collation utf8mb4_0900_ai_ci 1
```

This closes the sampled Nüshu ngram parity question at these token sizes and
collations. It does not establish MeCab support, exact legacy weight bytes,
or parity for other Unicode ranges and query shapes.
