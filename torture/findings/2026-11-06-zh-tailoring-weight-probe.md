# Chinese 0900 Han tailoring and weight composition probe

The [maintained native oracle](../scripts/ngram-multicol-oracle.py) now has an
optional `--tailoring-probe` lane for `utf8mb4_zh_0900_as_cs`. It ranks
U+4E00–U+4EFF on MySQL 8.4.11 and fsdb, then maps each Han character to the
host ICU Han character at the same rank. This keeps transformed values in
ICU's Han region and tests whether a permutation can repair Chinese ordering
without changing the collation key representation.

The lane inserts the same 400 logical values into MySQL and two fsdb tables:
256 Han singletons, 16 other-script controls, and 128 Han/ASCII or Han/Han
compositions. One fsdb table uses the original values; the other uses the
rank permutation. Each has an index on its Chinese-collated value.

| Candidate | Displaced ordered positions | Pairwise inversions | Wrong indexed `value < ?` ranges |
| --- | ---: | ---: | ---: |
| Current host ICU | 173 / 400 | 193 | 174 / 400 |
| Han-rank permutation | 12 / 400 | 8 | 13 / 400 |

The first remaining mixed-order disagreement places `丆` against the
supplementary ideograph `𠀀`. Other range mismatches include thresholds
`丘`, `亃`, `什`, and ideographic zero `〇`. The permutation is an improvement
on this corpus but is **not a correct production collation**: it does not
provide a consistent mixed-script order or indexed range membership.

The same lane tests whether MySQL's exposed `WEIGHT_STRING` for a string is
the concatenation of its singleton primary weights, followed by the
concatenated secondary and tertiary weights. This reconstruction matches
406 of 409 sampled strings, including Han with ASCII, kana, Hangul,
supplementary characters, and a decomposed accent. It differs for `重庆`,
`沈阳`, and `藏文`, which are tailored multi-character sequences. For example,
MySQL gives `一` primary weight `A63E` and `a` primary weight `BDC4`;
`一a` begins with `A63EBDC4`. The three exceptions show that a production
key generator also needs contraction tailoring.

MySQL's [Chinese collation data header](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/strings/uca900_zh_data.h)
names CLDR 30 tailoring and generated Han weight pages. The next production
step is to derive one consistent Chinese key source for comparison, equality,
indexed ranges, and `WEIGHT_STRING`, including mixed scripts and the tailored
sequences, then validate the broader Han and non-Han corpus against MySQL.
Full-text membership and relevance remain separately correct in the audited
96-search lane. No engine change was made from this probe.

Reproduce after building the Release CLI:

```sh
uv run --with pymysql torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 4E00:4F00 --legacy-term-range 4E00:4E10 \
  --collation utf8mb4_zh_0900_as_cs --order-checks --tailoring-probe 1
```

The command exits nonzero because it also reports the current production
collation mismatch. The probe's before/after counts are diagnostic output;
the permutation is not applied to fsdb production code.
