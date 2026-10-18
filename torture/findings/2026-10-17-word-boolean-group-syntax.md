# Ordinary Boolean FULLTEXT group syntax

MySQL 8.4.11 rejects unbalanced parentheses in an ordinary word-parser
`MATCH ... AGAINST(... IN BOOLEAN MODE)` query with error 1064. An unmatched
open parenthesis reports `syntax error, unexpected $end`; an unmatched close
reports `syntax error, unexpected ')', expecting $end`. The same query under
the ngram parser returns no matches instead. Unclosed double quotes remain
accepted by the word parser, and a balanced group still finds its term.

Fsdb previously ignored a stray group delimiter and could return matching
rows. The Boolean parser now shares one quote-aware parenthesis check: ngram
queries retain their empty-result behavior, while the executor returns the
MySQL word-parser error before scoring. The
[native oracle](../scripts/fulltext-boolean-syntax-oracle.py) and focused
executor regression cover four malformed forms, the exact error messages,
the ngram distinction, balanced groups, and unclosed-quote controls.
