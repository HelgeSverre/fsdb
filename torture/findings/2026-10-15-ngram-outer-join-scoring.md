# Ngram scoring on the nullable side of an outer join

MySQL 8.4.11 is the oracle. With 1,000 two-column ngram documents, ten rows
have `owner_id=42` and a matching `生日` term. A left join from owners 42 and
43 to documents, with `d.owner_id=42` in `ON`, returns those ten rows with
relevance `0.009392`. If `MATCH` is also in `WHERE`, owner 43 is excluded. If
`MATCH` is only projected, owner 43 remains as a null-extended row and its
score is `0`, not `NULL`. Reversing the join as a right join gives the same
eleven rows. A direct `MATCH(body,title) AGAINST(NULL IN BOOLEAN MODE)` also
returns zero on the sampled document.

FSDB previously matched the filtered rows but prepared 1,600 virtual-column
values in the focused fixture even though only ten documents could join.
It also projected `NULL` for the unmatched owner's synthetic score. The
full-text pre-pass now uses simple right-local `ON` predicates of a left join
as scoring candidate bounds. It keeps the original join and `ON` evaluation,
so the preserved left source is not filtered. Score references from either
nullable join side now turn a null-extended synthetic score into zero.

The [native oracle](../scripts/fulltext-join-bounds-oracle.py) checks both
orientations, the filtered query, relevance, and the unmatched row. The focused
Expecto regression checks the same results and requires fewer than 30 virtual
preparations for the filtered left join. Broader CJK join graphs and MeCab
remain open.
