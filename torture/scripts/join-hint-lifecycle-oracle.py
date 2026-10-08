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

def verify(client, _writer):
    arguments["verify_cases"](client, [(setup + sql, expected) for sql, expected in cases])

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
