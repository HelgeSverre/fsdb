"""Pin repeated CTE syntax warnings and SQL-mode-aware hint diagnostics."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [('WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1;SHOW WARNINGS',
  'n\tn\n'
  '1\t1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1' "
  'at line 1\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n)' at line 1\n"),
 ('WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT n FROM c;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT n FROM c' at line 1\n"),
 ('WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1 JOIN c d ON 1;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1 JOIN "
  "c d ON 1' at line 1\n"
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n)' at line 1\n"
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n)' at line 1\n"),
 ('WITH c AS (\n SELECT /*+ BOGUS */ 1 AS n\n) SELECT a.n,b.n FROM c a JOIN c b ON 1;SHOW WARNINGS',
  'n\tn\n'
  '1\t1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n\n"
  ") SELECT a.n,b.n FROM c a JOIN c b ON 1' at line 2\n"
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n\n"
  ")' at line 2\n"),
 ('WITH c AS (SELECT /*+ BOGUS */ 1 AS n   )   SELECT a.n,b.n FROM c a JOIN c b ON 1;SHOW WARNINGS',
  'n\tn\n'
  '1\t1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n   )   SELECT a.n,b.n FROM c a JOIN c b "
  "ON 1' at line 1\n"
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n   )' at line 1\n"),
 ('WITH c AS (SELECT /*+ BOGUS */ 1 AS n), d AS (SELECT /*+ OTHER */ n FROM c) SELECT a.n FROM d a JOIN d b '
  'ON 1;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n), d AS (SELECT /*+ OTHER */ n FROM c) "
  "SELECT a.n FROM d a JOIN d' at line 1\n"
  "Warning\t1064\tOptimizer hint syntax error near 'OTHER */ n FROM c) SELECT a.n FROM d a JOIN d b ON 1' at "
  'line 1\n'
  "Warning\t1064\tOptimizer hint syntax error near 'OTHER */ n FROM c)' at line 1\n"
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n)' at line 1\n"),
 ('WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT /*+ BKA(x) */ a.n FROM c a JOIN c b ON 1;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT /*+ BKA(x) */ a.n FROM c a JOIN "
  "c b ON 1' at line 1\n"
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n)' at line 1\n"
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1;SHOW WARNINGS;SELECT 1 AS n;SHOW '
  'WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1' at "
  'line 1\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n)' at line 1\n"
  'n\n'
  '1\n'),
 ('WITH RECURSIVE c AS (SELECT /*+ BOGUS */ 1 AS n UNION ALL SELECT n+1 FROM c WHERE n<2) SELECT n FROM '
  'c;SHOW WARNINGS',
  'n\n'
  '1\n'
  '2\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n UNION ALL SELECT n+1 FROM c WHERE n<2) "
  "SELECT n FROM c' at line 1\n"),
 ('WITH RECURSIVE c AS (SELECT /*+ BOGUS */ 1 AS n UNION ALL SELECT n+1 FROM c WHERE n<2) SELECT a.n FROM c '
  'a JOIN c b ON a.n=b.n ORDER BY a.n;SHOW WARNINGS',
  'n\n'
  '1\n'
  '2\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n UNION ALL SELECT n+1 FROM c WHERE n<2) "
  "SELECT a.n FROM c a JOIN ' at line 1\n"
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n UNION ALL SELECT n+1 FROM c WHERE n<2)' "
  'at line 1\n'),
 ('WITH c AS (WITH u AS (SELECT /*+ BOGUS */ 1 AS n) SELECT 1 AS n) SELECT a.n FROM c a JOIN c b ON 1;SHOW '
  'WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT 1 AS n) SELECT a.n FROM c a JOIN "
  "c b ON 1' at line 1\n"
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT 1 AS n)' at line 1\n"),
 ("PREPARE s FROM 'WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1';SHOW "
  'WARNINGS;EXECUTE s;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1' at "
  'line 1\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n)' at line 1\n"
  'n\n'
  '1\n'),
 ('WITH c AS (SELECT /*+ MAX_EXECUTION_TIME(18446744073709551616) */ 1 AS n) SELECT a.n FROM c a JOIN c b ON '
  '1;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tUnsupported MAX_EXECUTION_TIME near ') */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1' at "
  'line 1\n'
  "Warning\t1064\tUnsupported MAX_EXECUTION_TIME near ') */ 1 AS n)' at line 1\n"),
 ('WITH c AS (SELECT /*+ QB_NAME(q) QB_NAME(r) BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1;SHOW '
  'WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1' at "
  'line 1\n'
  'Warning\t3126\tHint QB_NAME(`r`) is ignored as conflicting/duplicated\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n)' at line 1\n"
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'),
 ('\n\nWITH c AS (\n SELECT /*+ BOGUS */ 1 AS n\n) SELECT a.n FROM c a JOIN c b ON 1;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n\n"
  ") SELECT a.n FROM c a JOIN c b ON 1' at line 2\n"
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n\n"
  ")' at line 2\n"),
 ('WITH c AS (WITH u AS (SELECT /*+ BOGUS */ 1 AS n) SELECT a.n FROM u a JOIN u b ON 1) SELECT a.n FROM c a '
  'JOIN c b ON 1;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT a.n FROM u a JOIN u b ON 1) "
  "SELECT a.n FROM c a JOIN c b' at line 1\n"
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n)' at line 1\n"
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT a.n FROM u a JOIN u b ON 1)' at "
  'line 1\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n)' at line 1\n"),
 ('SET sql_mode=\'ANSI_QUOTES\';WITH c AS (SELECT /*+ BOGUS */ 1 AS "n") SELECT a."n" FROM c a JOIN c b ON '
  '1;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t1064\tOptimizer hint syntax error near \'BOGUS */ 1 AS "n") SELECT a."n" FROM c a JOIN c b ON '
  "1' at line 1\n"
  'Warning\t1064\tOptimizer hint syntax error near \'BOGUS */ 1 AS "n")\' at line 1\n'),
 ("WITH c AS (SELECT /*+ BOGUS */ ')' AS n) SELECT a.n FROM c a JOIN c b ON 1;SHOW WARNINGS",
  'n\n'
  ')\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ ')' AS n) SELECT a.n FROM c a JOIN c b ON 1' at "
  'line 1\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ ')' AS n)' at line 1\n"),
 ('WITH c AS (SELECT /*+ BOGUS */ /*!80000 1 */ AS n) SELECT a.n FROM c a JOIN c b ON 1;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ /*!80000 1 */ AS n) SELECT a.n FROM c a JOIN c "
  "b ON 1' at line 1\n"
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */  1  AS n)' at line 1\n"),
 ('/*!80000 */ WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1' at "
  'line 1\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n)' at line 1\n")]

cases += [("SET sql_mode='';SELECT /*+ QB_NAME(q) QB_NAME(r) BKA(t) NO_BKA(t) NO_INDEX(t i) BKA(@missing t) "
  '*/ 1;SHOW WARNINGS',
  '1\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`r`) is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint NO_BKA(`t` ) is ignored as conflicting/duplicated\n'
  'Warning\t3127\tQuery block name `missing` is not found for BKA hint\n'
  'Warning\t3128\tUnresolved name `t`@`q` for BKA hint\n'
  'Warning\t3128\tUnresolved name `t`@`q` for NO_INDEX hint\n'
  'Warning\t3128\tUnresolved name `t`@`q` `i` for NO_INDEX hint\n'),
 ("SET sql_mode='ANSI_QUOTES';SELECT /*+ QB_NAME(q) QB_NAME(r) BKA(t) NO_BKA(t) NO_INDEX(t i) "
  'BKA(@missing t) */ 1;SHOW WARNINGS',
  '1\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME("r") is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint NO_BKA("t" ) is ignored as conflicting/duplicated\n'
  'Warning\t3127\tQuery block name "missing" is not found for BKA hint\n'
  'Warning\t3128\tUnresolved name "t"@"q" for BKA hint\n'
  'Warning\t3128\tUnresolved name "t"@"q" for NO_INDEX hint\n'
  'Warning\t3128\tUnresolved name "t"@"q" "i" for NO_INDEX hint\n')]

def verify(client, _writer):
    arguments["verify_cases"](client, cases)

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
