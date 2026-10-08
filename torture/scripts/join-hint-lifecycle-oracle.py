"""Pin native join-order hint warning phases and optimization short circuits."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
setup = "CREATE TEMPORARY TABLE t(n INT);INSERT INTO t VALUES(1);"
cases = [('SELECT /*+ JOIN_ORDER(x) */ 1 AS n;SHOW WARNINGS', 'n\n1\n'),
 ('SELECT /*+ JOIN_PREFIX(x) JOIN_SUFFIX(y) */ 1 AS n;SHOW WARNINGS', 'n\n1\n'),
 ('SELECT /*+ JOIN_PREFIX(x) JOIN_PREFIX(y) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint JOIN_PREFIX( `y`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ JOIN_FIXED_ORDER() JOIN_ORDER(x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint JOIN_ORDER( `x`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ JOIN_ORDER(@missing x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3127\tQuery block name `missing` is not found for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x@missing) */ 1 AS n;SHOW WARNINGS', 'n\n1\n'),
 ('SELECT /*+ BKA(x) NO_INDEX(x i) JOIN_ORDER(y) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for NO_INDEX hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `i` for NO_INDEX hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM DUAL;SHOW WARNINGS', 'n\n1\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM (SELECT 1 AS n) d;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM t;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM t WHERE 0;SHOW WARNINGS', ''),
 ('SELECT /*+ JOIN_ORDER(x) */ (SELECT n FROM t) AS n;SHOW WARNINGS', 'n\n1\n'),
 ('SELECT (SELECT /*+ JOIN_ORDER(x) */ 1) AS n FROM t;SHOW WARNINGS', 'n\n1\n'),
 ('WITH c AS (SELECT /*+ JOIN_ORDER(x) */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1;SHOW WARNINGS',
  'n\n1\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n UNION ALL SELECT 2;SHOW WARNINGS', 'n\n1\n2\n'),
 ("PREPARE s FROM 'SELECT /*+ JOIN_ORDER(x) */ 1 AS n';SHOW WARNINGS;EXECUTE s;SHOW WARNINGS",
  'n\n1\n'),
 ('SELECT /*+ QB_NAME(q) JOIN_PREFIX(@q x) JOIN_SUFFIX(x@q) */ 1 AS n;SHOW WARNINGS', 'n\n1\n'),
 ("SET sql_mode='ANSI_QUOTES';SELECT /*+ JOIN_PREFIX(x) JOIN_PREFIX(y) JOIN_ORDER(@missing x) */ 1 "
  'AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint JOIN_PREFIX( "y") is ignored as conflicting/duplicated\n'
  'Warning\t3127\tQuery block name "missing" is not found for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) BKA(y) */ 1 AS n FROM t WHERE FALSE;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) BKA(y) */ 1 AS n FROM t WHERE NULL;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) BKA(y) */ 1 AS n FROM t WHERE 1=0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) BKA(y) */ 1 AS n FROM t WHERE n<0;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) BKA(y) */ 1 AS n FROM t LIMIT 0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) BKA(y) */ 1 AS n FROM t WHERE 1=1;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) BKA(y) */ 1 AS n FROM t HAVING 0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) BKA(y) */ 1 AS n FROM t WHERE 0 OR n=1;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM t WHERE 0';SHOW WARNINGS;EXECUTE s;SHOW "
  'WARNINGS',
  ''),
 ("PREPARE s FROM 'SELECT /*+ JOIN_ORDER(x) BKA(y) */ 1 AS n FROM t';SHOW WARNINGS;SELECT "
  "'after_prepare' AS phase;EXECUTE s;SHOW WARNINGS",
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'
  'phase\n'
  'after_prepare\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ JOIN_ORDER(x) BKA(y) */ 1 AS n FROM t WHERE 0';SHOW WARNINGS;SELECT "
  "'after_prepare' AS phase;EXECUTE s;SHOW WARNINGS",
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'
  'phase\n'
  'after_prepare\n'),
 ("PREPARE s FROM 'SELECT /*+ JOIN_ORDER(x) BKA(y) */ 1 AS n FROM t LIMIT 0';SHOW WARNINGS;SELECT "
  "'after_prepare' AS phase;EXECUTE s;SHOW WARNINGS",
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'
  'phase\n'
  'after_prepare\n'),
 ("PREPARE s FROM 'SELECT /*+ JOIN_ORDER(x) */ n FROM t';SHOW WARNINGS;EXECUTE s;SHOW "
  'WARNINGS;EXECUTE s;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ JOIN_ORDER(x) */ n FROM t WHERE ?';SHOW WARNINGS;SET @p=0;EXECUTE s "
  'USING @p;SHOW WARNINGS;SET @p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ JOIN_ORDER(x) */ n FROM t LIMIT ?';SHOW WARNINGS;SET @p=0;EXECUTE s "
  'USING @p;SHOW WARNINGS;SET @p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('DELETE FROM t;SELECT /*+ JOIN_ORDER(x) */ n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n FROM t WHERE 0;SHOW WARNINGS', 'n\n0\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM t WHERE n=1 AND n=2;SHOW WARNINGS', '')]

cases += [('SELECT /*+ JOIN_ORDER(x) */ SQL_CALC_FOUND_ROWS n FROM t LIMIT 0;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t1287\tSQL_CALC_FOUND_ROWS is deprecated and will be removed in a future release. '
  'Consider using two separate queries instead.\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM t WHERE 0 AND n=1;SHOW WARNINGS', ''),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM t WHERE n=1 AND n=1;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM t WHERE n=1 AND n=2 OR n=1;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM t WHERE n=1 AND n=2 OR 0;SHOW WARNINGS', ''),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM t WHERE n=1 AND n=2 UNION ALL SELECT 2;SHOW WARNINGS',
  'n\n2\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM t HAVING COUNT(*)=0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM t WHERE 1/0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t1365\tDivision by 0\n'),
 ('SELECT IF(0,(SELECT /*+ JOIN_ORDER(x) */ n FROM t),1) AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT (SELECT /*+ JOIN_ORDER(x) */ n FROM t) AS n WHERE 0;SHOW WARNINGS', ''),
 ('WITH c AS (SELECT /*+ JOIN_ORDER(x) */ n FROM (SELECT 1 AS n) t) SELECT a.n FROM c a JOIN c b '
  'ON a.n=b.n;SHOW WARNINGS',
  'n\n1\n'),
 ('WITH c AS (SELECT /*+ JOIN_ORDER(x) */ n FROM (SELECT 1 AS n) t WHERE 0) SELECT a.n FROM c a '
  'JOIN c b ON a.n=b.n;SHOW WARNINGS',
  ''),
 ('SELECT /*+ JOIN_ORDER(x) */ 1 AS n FROM t WHERE 0;SELECT /*+ JOIN_ORDER(x) */ n FROM t;SHOW '
  'WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ JOIN_ORDER(x) */ n FROM t WHERE ?+0';SHOW WARNINGS;SET @p=0;EXECUTE "
  's USING @p;SHOW WARNINGS;SET @p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n')]

cases += [('SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM t) d WHERE 0;SHOW WARNINGS', ''),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM t) d WHERE 0;SHOW '
  'WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ n FROM t UNION ALL SELECT 2 LIMIT 0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT 2 UNION ALL SELECT /*+ JOIN_ORDER(x) */ n FROM t LIMIT 0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ n FROM t WHERE n=1 AND n=2 OR n=1;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ n FROM t WHERE n=1 AND n=2 LIMIT 0;SHOW WARNINGS', ''),
 ("SELECT /*+ JOIN_ORDER(x) */ n FROM t WHERE 'x';SHOW WARNINGS",
  "Level\tCode\tMessage\nWarning\t1292\tTruncated incorrect DOUBLE value: 'x'\n"),
 ("PREPARE s FROM 'SELECT /*+ JOIN_ORDER(x) */ n FROM t WHERE ?';SHOW WARNINGS;SET @p=NULL;EXECUTE "
  's USING @p;SHOW WARNINGS;SET @p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) */ n FROM t HAVING 1/0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t1365\tDivision by 0\n')]

cases += [('CREATE TEMPORARY TABLE f(v DOUBLE);INSERT INTO f VALUES(9007199254740992);SELECT /*+ '
  'JOIN_ORDER(x) */ 1 AS n FROM f WHERE v=9007199254740992 AND v=9007199254740993;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('CREATE TEMPORARY TABLE f(v DECIMAL(20,0));INSERT INTO f VALUES(9007199254740992);SELECT /*+ '
  'JOIN_ORDER(x) */ 1 AS n FROM f WHERE v=9007199254740992 AND v=9007199254740993;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('CREATE TEMPORARY TABLE f(v DOUBLE);INSERT INTO f VALUES(1);SELECT /*+ JOIN_ORDER(x) */ 1 AS n '
  'FROM f WHERE v=1 AND v=2;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n')]

def verify(client, _writer):
    arguments["verify_cases"](client, [(setup + sql, expected) for sql, expected in cases])

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
