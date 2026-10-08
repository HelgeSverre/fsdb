"""Pin CTE hint instantiation and lexical scope on native MySQL 8.4.11."""
import pathlib
import runpy
import subprocess

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT 1 AS n;SHOW WARNINGS', 'n\n1\n'),
 ('WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) SELECT @@max_points_in_geometry AS '
  'n;SHOW WARNINGS',
  'n\n65536\n'),
 ('WITH c AS (SELECT /*+ QB_NAME(q) QB_NAME(r) */ 1 AS n) SELECT /*+ QB_NAME(q) QB_NAME(r) */ 1 AS n;SHOW '
  'WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3126\tHint QB_NAME(`r`) is ignored as conflicting/duplicated\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT (SELECT /*+ BKA(y) */ n FROM c) AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#3` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n), d AS (SELECT /*+ BKA(y) */ n FROM c) SELECT /*+ BKA(z) */ n FROM '
  'd;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `z`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#3` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1;SHOW WARNINGS',
  'n\tn\n'
  '1\t1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#3` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) SELECT (WITH c AS (SELECT 2 AS n) '
  'SELECT n FROM c) AS n,@@max_points_in_geometry AS limit_value;SHOW WARNINGS',
  'n\tlimit_value\n2\t65536\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT (WITH c AS (SELECT /*+ BKA(y) */ 2 AS n) SELECT n FROM c) '
  'AS n;SHOW WARNINGS',
  'n\n2\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) UPDATE t SET id=(WITH c AS (SELECT 2 '
  'AS n) SELECT n FROM c);SHOW WARNINGS;SELECT id,@@max_points_in_geometry AS limit_value FROM t;SHOW '
  'WARNINGS',
  'id\tlimit_value\n2\t65536\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) UPDATE t SET id=(WITH c AS (SELECT /*+ BKA(y) */ 2 AS n) SELECT n '
  'FROM c);SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT (SELECT /*+ BKA(y) */ 2) AS n FROM c;SHOW WARNINGS',
  'n\n'
  '2\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#3` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#2` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT 1 AS n UNION ALL SELECT n FROM c;SHOW WARNINGS',
  'n\n1\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`select#3` for BKA hint\n'),
 ('WITH RECURSIVE c AS (SELECT /*+ BKA(x) */ 1 AS n UNION ALL SELECT /*+ BKA(y) */ n+1 FROM c WHERE n<2) '
  'SELECT n FROM c;SHOW WARNINGS',
  'n\n'
  '1\n'
  '2\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT 1 AS n' at line 1\n"),
 ('WITH c AS (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1 AS n) SELECT 1 AS n;SHOW WARNINGS', 'n\n1\n'),
 ('WITH c AS (SELECT /*+ SET_VAR(unknown=1) */ 1 AS n) SELECT 1 AS n;SHOW WARNINGS', 'n\n1\n'),
 ('WITH c AS (SELECT /*+ QB_NAME(q) BKA(x) */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1;SHOW WARNINGS',
  'n\tn\n'
  '1\t1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`q` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#3` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) SELECT @@max_points_in_geometry AS n '
  'FROM c a JOIN c b ON 1;SHOW WARNINGS',
  'n\n'
  '7\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=7)  is ignored as conflicting/duplicated\n'),
 ('WITH c AS (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1;SHOW '
  'WARNINGS',
  'n\tn\n'
  '1\t1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'),
 ('WITH c AS (SELECT /*+ SET_VAR(unknown=1) */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1;SHOW WARNINGS',
  'n\tn\n'
  '1\t1\n'
  'Level\tCode\tMessage\n'
  "Warning\t3128\tUnresolved name 'unknown' for SET_VAR hint\n"
  "Warning\t3128\tUnresolved name 'unknown' for SET_VAR hint\n"),
 ('WITH c AS (SELECT /*+ QB_NAME(q) QB_NAME(r) */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1;SHOW '
  'WARNINGS',
  'n\tn\n'
  '1\t1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`r`) is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT /*+ NO_MERGE(a) NO_MERGE(b) */ a.n,b.n FROM c a JOIN c b '
  'ON 1;SHOW WARNINGS',
  'n\tn\n'
  '1\t1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#3` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT n FROM c UNION ALL SELECT n FROM c;SHOW WARNINGS',
  'n\n'
  '1\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#4` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n), d AS (SELECT /*+ BKA(y) */ n FROM c) SELECT a.n,b.n FROM d a '
  'JOIN d b ON 1;SHOW WARNINGS',
  'n\tn\n'
  '1\t1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#3` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#4` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#5` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n), d AS (SELECT n FROM c) SELECT '
  '@@max_points_in_geometry AS n;SHOW WARNINGS',
  'n\n65536\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT (WITH d AS (SELECT /*+ BKA(y) */ n FROM c) SELECT n FROM '
  'd) AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#4` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) UPDATE t JOIN c a ON 1 JOIN c b ON 1 SET id=a.n+b.n;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#3` for BKA hint\n'),
 ('WITH RECURSIVE c AS (SELECT /*+ QB_NAME(q) BKA(x) */ 1 AS n UNION ALL SELECT /*+ BKA(y) */ n+1 FROM c '
  'WHERE n<2) SELECT a.n,b.n FROM c a JOIN c b ON a.n=b.n ORDER BY a.n;SHOW WARNINGS',
  'n\tn\n'
  '1\t1\n'
  '2\t2\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`q` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#6` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#7` for BKA hint\n'),
 ("PREPARE s FROM 'WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1';SHOW "
  'WARNINGS;EXECUTE s;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#3` for BKA hint\n'
  'n\tn\n'
  '1\t1\n'),
 ('WITH c AS (SELECT /*+ QB_NAME(q) BKA(x) */ 1 AS n) SELECT /*+ BKA(a@q) */ a.n,b.n FROM c a JOIN c b ON '
  '1;SHOW WARNINGS',
  'n\tn\n'
  '1\t1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`q` for BKA hint\n'
  'Warning\t3128\tUnresolved name `a`@`q` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#3` for BKA hint\n'),
 ('WITH a AS (SELECT /*+ BKA(x) */ 1 AS n), b AS (SELECT /*+ BKA(y) */ 2 AS n) SELECT (SELECT n FROM b) AS n '
  'FROM a;SHOW WARNINGS',
  'n\n'
  '2\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#4` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT (WITH c AS (SELECT /*+ BKA(y) */ 2 AS n) SELECT n FROM c) '
  'AS n FROM c;SHOW WARNINGS',
  'n\n'
  '2\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#4` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(c) */ 1 AS n) SELECT a.n FROM c a JOIN (SELECT /*+ BKA(d) */ 2 AS n) d ON '
  '1;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `c`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `d`@`select#3` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(c) */ 1 AS n) SELECT (SELECT /*+ BKA(s) */ n FROM c) AS n FROM (SELECT /*+ '
  'BKA(d) */ 2 AS n) d;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `d`@`select#4` for BKA hint\n'
  'Warning\t3128\tUnresolved name `s`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `c`@`select#3` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(c) */ 1 AS n) UPDATE t JOIN c ON 1 SET id=(SELECT /*+ BKA(s) */ 1);SHOW '
  'WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `c`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `s`@`select#3` for BKA hint\n'),
 ('WITH RECURSIVE c AS (SELECT /*+ BKA(x) */ 1 AS n UNION ALL SELECT /*+ BKA(y) */ n+1 FROM c WHERE n<2) '
  'SELECT a.n FROM c a JOIN c b ON a.n=b.n JOIN c d ON a.n=d.n ORDER BY a.n;SHOW WARNINGS',
  'n\n'
  '1\n'
  '2\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#6` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#7` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#a` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#b` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ QB_NAME(q) */ 1 AS n) SELECT (SELECT /*+ QB_NAME(s) */ n FROM c) AS n FROM (SELECT '
  '/*+ QB_NAME(d) */ 2 AS n) d;SHOW WARNINGS',
  'n\n1\n'),
 ('WITH RECURSIVE c AS (SELECT /*+ BKA(x) */ 1 AS n UNION ALL SELECT /*+ BKA(y) */ n+1 FROM c WHERE n<2 '
  'UNION ALL SELECT /*+ BKA(z) */ n+2 FROM c WHERE n<2) SELECT a.n FROM c a JOIN c b ON a.n=b.n ORDER BY '
  'a.n;SHOW WARNINGS',
  'n\n'
  '1\n'
  '2\n'
  '3\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'
  'Warning\t3128\tUnresolved name `z`@`select#5` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#8` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#9` for BKA hint\n'
  'Warning\t3128\tUnresolved name `z`@`select#b` for BKA hint\n'),
 ('WITH RECURSIVE c AS (SELECT /*+ BKA(x) */ 1 AS n UNION ALL SELECT /*+ BKA(y) */ 2) SELECT a.n FROM c a '
  'JOIN c b ON a.n=b.n ORDER BY a.n;SHOW WARNINGS',
  'n\n'
  '1\n'
  '2\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#5` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#6` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT 1 AS n FROM (WITH c AS (SELECT /*+ BKA(y) */ 2 AS n) '
  'SELECT n FROM c) d JOIN c a ON 1;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#4` for BKA hint\n'),
 ('WITH a AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n), b AS (SELECT /*+ '
  'SET_VAR(max_points_in_geometry=9) */ n FROM a) SELECT @@max_points_in_geometry AS n FROM b x JOIN b y ON '
  '1;SHOW WARNINGS',
  'n\n'
  '7\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=7)  is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'),
 ('SELECT (WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) SELECT 1) AS '
  'n,@@max_points_in_geometry AS limit_value;SHOW WARNINGS',
  'n\tlimit_value\n1\t65536\n'),
 ('SELECT (WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) SELECT a.n FROM c a JOIN c b '
  'ON 1) AS n,@@max_points_in_geometry AS limit_value;SHOW WARNINGS',
  'n\tlimit_value\n'
  '1\t7\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=7)  is ignored as conflicting/duplicated\n'),
 ('SELECT (WITH c AS (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1 AS n) SELECT 1) AS n;SHOW WARNINGS', 'n\n1\n')]

def verify(client, _writer):
    for case in cases:
        subprocess.run([*client.process.args, "-e", "USE probe;DROP TABLE IF EXISTS t;CREATE TABLE t(id INT);INSERT INTO t VALUES(1)"], capture_output=True, text=True, check=True)
        arguments["verify_cases"](client, [case])

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
