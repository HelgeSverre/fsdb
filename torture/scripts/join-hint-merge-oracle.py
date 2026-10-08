"""Pin native join-order diagnostics across derived and CTE merging."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
setup = "DROP TABLE IF EXISTS merge_base;CREATE TABLE merge_base(n INT);INSERT INTO merge_base VALUES(1);"
cases = [('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d;SHOW WARNINGS', 'n\n1\n'),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d;SHOW '
  'WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d WHERE 0;SHOW WARNINGS', ''),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d WHERE '
  '0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM merge_base) d;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM merge_base) '
  'd;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM merge_base) d WHERE 0;SHOW '
  'WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM merge_base) d '
  'WHERE 0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base LIMIT 1) d;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base LIMIT 1) '
  'd;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base LIMIT 1) d WHERE 0;SHOW '
  'WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base LIMIT 1) d '
  'WHERE 0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n FROM merge_base) d;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n FROM merge_base) '
  'd;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n FROM merge_base) d WHERE 0;SHOW '
  'WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n FROM merge_base) '
  'd WHERE 0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base WHERE 0) d;SHOW WARNINGS', ''),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base WHERE 0) '
  'd;SHOW WARNINGS',
  ''),
 ('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base WHERE 0) d WHERE 0;SHOW '
  'WARNINGS',
  ''),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base WHERE 0) d '
  'WHERE 0;SHOW WARNINGS',
  ''),
 ('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ 1 AS n) d;SHOW WARNINGS', 'n\n1\n'),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ 1 AS n) d;SHOW WARNINGS',
  'n\n1\n'),
 ('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ 1 AS n) d WHERE 0;SHOW WARNINGS', ''),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ 1 AS n) d WHERE 0;SHOW WARNINGS',
  ''),
 ('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM (SELECT 1 AS n) t) d;SHOW WARNINGS',
  'n\n1\n'),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM (SELECT 1 AS n) t) '
  'd;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM (SELECT 1 AS n) t) d WHERE 0;SHOW WARNINGS',
  ''),
 ('SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM (SELECT 1 AS n) t) d '
  'WHERE 0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('WITH c AS (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) SELECT n FROM c;SHOW WARNINGS',
  'n\n1\n'),
 ('WITH c AS (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) SELECT a.n FROM c a JOIN c b ON '
  'a.n=b.n;SHOW WARNINGS',
  'n\n1\n'),
 ('WITH c AS (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) SELECT /*+ NO_MERGE(a) NO_MERGE(b) */ '
  'a.n FROM c a JOIN c b ON a.n=b.n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM merge_base) d;SHOW '
  'WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT d.n FROM (SELECT /*+ BKA(x) JOIN_ORDER(y) */ n FROM merge_base) d;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'),
 ("SET optimizer_switch='derived_merge=off';SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM "
  'merge_base) d;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n')]

cases += [('SELECT /*+ NO_MERGE(d) MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) '
  'd;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint MERGE(`d` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ MERGE(d) NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) '
  'd;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_MERGE(`d` ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ NO_MERGE() */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d;SHOW '
  'WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ QB_NAME(q) NO_MERGE(d@q) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) '
  'd;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ NO_MERGE(@q d) QB_NAME(q) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM '
  'merge_base) d;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3127\tQuery block name `q` is not found for NO_MERGE hint\n'),
 ('SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base GROUP BY n) d WHERE 0;SHOW '
  'WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base HAVING 1) d WHERE 0;SHOW '
  'WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base ORDER BY n) d;SHOW WARNINGS',
  'n\n1\n'),
 ('SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ ROW_NUMBER() OVER() AS n FROM merge_base) d WHERE '
  '0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ (SELECT 1) AS n FROM merge_base) d WHERE 0;SHOW '
  'WARNINGS',
  ''),
 ('SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base UNION ALL SELECT 2) d WHERE '
  '0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('WITH c AS (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM merge_base) SELECT n FROM c;SHOW '
  'WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('WITH RECURSIVE c AS (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base UNION ALL SELECT n+1 FROM c '
  'WHERE n<2) SELECT n FROM c ORDER BY n;SHOW WARNINGS',
  'n\n1\n2\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ NO_MERGE(d) JOIN_ORDER(parent_missing) */ d.n FROM (SELECT /*+ '
  'JOIN_ORDER(child_missing) */ n FROM merge_base) d;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `child_missing` for JOIN_ORDER hint\n'
  'Warning\t3128\tUnresolved name `parent_missing` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_ORDER(parent_missing) */ (SELECT /*+ JOIN_ORDER(child_missing) */ n FROM '
  'merge_base) AS n FROM merge_base;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `parent_missing` for JOIN_ORDER hint\n'
  'Warning\t3128\tUnresolved name `child_missing` for JOIN_ORDER hint\n'),
 ('SELECT /*+ MERGE(d) NO_MERGE() */ d.n FROM (SELECT /*+ JOIN_ORDER(child_missing) */ n FROM '
  'merge_base) d;SHOW WARNINGS',
  'n\n1\n'),
 ('SELECT /*+ NO_MERGE(d) MERGE() */ d.n FROM (SELECT /*+ JOIN_ORDER(child_missing) */ n FROM '
  'merge_base) d;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `child_missing` for JOIN_ORDER hint\n'),
 ('SELECT /*+ NO_MERGE() MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(child_missing) */ n FROM '
  'merge_base) d;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint MERGE(`d` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `child_missing` for JOIN_ORDER hint\n')]

cases += [("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM "
  "merge_base) d WHERE 0';SHOW WARNINGS;SET @p=0;EXECUTE s;SHOW WARNINGS;SET @p=1;EXECUTE s;SHOW "
  'WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM "
  "merge_base) d WHERE ?';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET @p=1;EXECUTE "
  's USING @p;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM "
  "merge_base) d WHERE ?+0';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET "
  '@p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM "
  "merge_base) d WHERE 0 AND ?';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET "
  '@p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM "
  "merge_base) d HAVING ?';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET "
  '@p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM "
  "merge_base) d LIMIT ?';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET @p=1;EXECUTE "
  's USING @p;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM "
  "merge_base) d WHERE 0';SHOW WARNINGS;SET @p=0;EXECUTE s;SHOW WARNINGS;SET @p=1;EXECUTE s;SHOW "
  'WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM "
  "merge_base) d WHERE ?';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET @p=1;EXECUTE "
  's USING @p;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM "
  "merge_base) d WHERE ?+0';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET "
  '@p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM "
  "merge_base) d WHERE 0 AND ?';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET "
  '@p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM "
  "merge_base) d HAVING ?';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET "
  '@p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM "
  "merge_base) d LIMIT ?';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET @p=1;EXECUTE "
  's USING @p;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n "
  "FROM merge_base) d WHERE 0';SHOW WARNINGS;SET @p=0;EXECUTE s;SHOW WARNINGS;SET @p=1;EXECUTE "
  's;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n "
  "FROM merge_base) d WHERE ?';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET "
  '@p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n "
  "FROM merge_base) d WHERE ?+0';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET "
  '@p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n "
  "FROM merge_base) d WHERE 0 AND ?';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET "
  '@p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n "
  "FROM merge_base) d HAVING ?';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET "
  '@p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ("PREPARE s FROM 'SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n "
  "FROM merge_base) d LIMIT ?';SHOW WARNINGS;SET @p=0;EXECUTE s USING @p;SHOW WARNINGS;SET "
  '@p=1;EXECUTE s USING @p;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n')]

def verify(client, _writer):
    arguments["verify_cases"](client, [(setup + sql, expected) for sql, expected in cases])

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
