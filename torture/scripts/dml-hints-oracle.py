"""Pin mutation hint ownership and diagnostics on native MySQL 8.4.11."""
import pathlib
import runpy
import subprocess

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [('UPDATE /*+ BKA(x) */ t SET id=id;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('UPDATE /*+ BKA(t) NO_INDEX(t missing) */ t SET id=id;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`select#1` `missing` for NO_INDEX hint\n'),
 ('UPDATE /*+ BKA(t) BKA(a) */ t AS a SET id=id;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`select#1` for BKA hint\n'),
 ('DELETE /*+ BKA(x) */ FROM t WHERE 0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('DELETE /*+ QB_NAME(q) BKA(x@q) */ FROM t WHERE 0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`q` for BKA hint\n'),
 ('INSERT /*+ BKA(x) */ INTO t VALUES(1);SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('REPLACE /*+ BKA(x) */ INTO t VALUES(1);SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('INSERT /*+ BKA(x) BKA(t) */ INTO t SELECT /*+ BKA(y) */ 1;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('REPLACE /*+ BKA(x) BKA(t) */ INTO t SELECT /*+ BKA(y) */ 1;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('UPDATE /*+ QB_NAME(q) QB_NAME(r) */ t SET id=(SELECT /*+ QB_NAME(q) BKA(x) */ 1);SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`q` for BKA hint\n'),
 ('UPDATE /*+ SET_VAR(max_points_in_geometry=9) */ t SET id=(SELECT /*+ SET_VAR(max_points_in_geometry=7) */ '
  '@@max_points_in_geometry);SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'),
 ('WITH c AS (SELECT /*+ BKA(y) */ 1 AS n) UPDATE /*+ BKA(x) */ t SET id=(SELECT n FROM c);SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'),
 ('DELETE /*+ BKA(x) */ FROM t WHERE id IN (SELECT /*+ BKA(y) */ n FROM (SELECT 1 AS n) a);SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#2` for BKA hint\n'),
 ('INSERT /*+ QB_NAME(q) QB_NAME(r) */ INTO t SELECT /*+ QB_NAME(q) BKA(x) */ 1;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint QB_NAME(`r`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`q` for BKA hint\n'),
 ('INSERT /*+ NO_INDEX(t missing) */ INTO t VALUES(1);SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`select#1` `missing` for NO_INDEX hint\n'),
 ('UPDATE /*+ BKA(x) NO_BKA(x) */ t SET id=id;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_BKA(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('DELETE /*+ BKA(x) NO_BKA(x) */ FROM t WHERE 0;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_BKA(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('INSERT /*+ BKA(x) NO_BKA(x) */ INTO t VALUES(1);SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_BKA(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('REPLACE /*+ QB_NAME(q) BKA(x@q) */ INTO t VALUES(1);SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`q` for BKA hint\n'),
 ('UPDATE /*+ BKA(a) BKA(t) BKA(b) */ t a JOIN t b ON a.id=b.id SET a.id=a.id;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`select#1` for BKA hint\n'),
 ('DELETE /*+ BKA(a) BKA(t) BKA(b) */ a FROM t a JOIN t b ON a.id=b.id WHERE 0;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`select#1` for BKA hint\n'),
 ('INSERT /*+ QB_NAME(q) BKA(x@q) */ INTO t VALUES((SELECT /*+ BKA(y) */ 1));SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`q` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#2` for BKA hint\n'),
 ('UPDATE t SET id=(SELECT /*+ BKA(x) */ 1);SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ QB_NAME(q) BKA(y) */ 1 AS n) UPDATE /*+ QB_NAME(q) BKA(x) */ t SET id=(SELECT /*+ '
  'QB_NAME(q) BKA(z) */ n FROM c);SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `z`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`q` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(y) */ 1 AS n) DELETE /*+ BKA(x) */ FROM t WHERE id IN (SELECT /*+ BKA(z) */ n '
  'FROM c);SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `z`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'),
 ('INSERT /*+ BKA(x) */ INTO t SELECT /*+ NO_BKA(x) */ 1;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint BKA(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for NO_BKA hint\n'),
 ('INSERT /*+ BKA(t) */ INTO t SELECT /*+ BKA(t) */ 1;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint BKA(`t` ) is ignored as conflicting/duplicated\n'),
 ('INSERT /*+ BKA(t) BKA(u) */ INTO t SELECT /*+ BKA(t) BKA(u) */ id FROM t u;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint BKA(`t` ) is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint BKA(`u` ) is ignored as conflicting/duplicated\n'),
 ('INSERT /*+ QB_NAME(rootq) BKA(x) */ INTO t SELECT /*+ QB_NAME(childq) BKA(y) */ 1;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`rootq`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `y`@`childq` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`childq` for BKA hint\n'),
 ('INSERT /*+ BKA(x) */ INTO t SELECT /*+ BKA(y) */ 1 UNION ALL SELECT /*+ BKA(z) */ 2;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `z`@`select#2` for BKA hint\n'),
 ('INSERT /*+ SET_VAR(max_points_in_geometry=9) */ INTO t SELECT /*+ SET_VAR(max_points_in_geometry=7) */ '
  '@@max_points_in_geometry;SHOW WARNINGS;SELECT id FROM t ORDER BY id;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'
  'id\n'
  '1\n'
  '7\n'),
 ('UPDATE /*+ SET_VAR(max_points_in_geometry=9) */ t SET id=(SELECT /*+ SET_VAR(max_points_in_geometry=7) */ '
  '@@max_points_in_geometry);SHOW WARNINGS;SELECT id FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'
  'id\n'
  '7\n'),
 ('INSERT /*+ BKA(x) */ INTO t VALUES(1) ON DUPLICATE KEY UPDATE id=(SELECT /*+ BKA(y) */ 1);SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#2` for BKA hint\n'),
 ('REPLACE /*+ BKA(x) */ INTO t SET id=(SELECT /*+ BKA(y) */ 1);SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#2` for BKA hint\n'),
 ("PREPARE s FROM 'UPDATE /*+ BKA(x) */ t SET id=id';SHOW WARNINGS;EXECUTE s;SHOW WARNINGS",
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('UPDATE /*+ QB_NAME(rootq) */ t SET id=(SELECT /*+ BKA(t@rootq) */ 1);SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `rootq` is not found for BKA hint\n'),
 ('WITH c AS (SELECT /*+ BKA(y) */ 1 AS n) UPDATE /*+ BKA(x) */ t SET id=id;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('UPDATE /*+ BKA(x) */ t JOIN (SELECT /*+ BKA(y) */ 1 AS n) a ON 1 SET id=(SELECT /*+ BKA(z) */ 1);SHOW '
  'WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `z`@`select#3` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ QB_NAME(q) QB_NAME(r) BKA(y) */ 1 AS n) UPDATE /*+ QB_NAME(q) BKA(x) */ t SET '
  'id=id;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`q` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) UPDATE /*+ '
  'SET_VAR(max_points_in_geometry=9) */ t SET id=@@max_points_in_geometry;SHOW WARNINGS;SELECT id FROM '
  't;SHOW WARNINGS',
  'id\n9\n'),
 ('INSERT /*+ QB_NAME(q) BKA(x) */ INTO t SELECT /*+ BKA(y) */ 1 UNION ALL SELECT /*+ QB_NAME(q) BKA(z) */ '
  '2;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `z`@`q` for BKA hint\n'),
 ('INSERT /*+ SET_VAR(max_points_in_geometry=9) */ INTO t SELECT 1 UNION ALL SELECT /*+ '
  'SET_VAR(max_points_in_geometry=7) */ @@max_points_in_geometry;SHOW WARNINGS;SELECT id FROM t ORDER BY '
  'id;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'
  'id\n'
  '1\n'
  '1\n'
  '7\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n), d AS (SELECT /*+ BKA(y) */ n FROM c) UPDATE /*+ BKA(z) */ t SET '
  'id=(SELECT n FROM d);SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `z`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#4` for BKA hint\n')]

def verify(client, _writer):
    for case in cases:
        subprocess.run([*client.process.args, "-e", "USE probe;DROP TABLE IF EXISTS t;CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id));INSERT INTO t VALUES(1)"], capture_output=True, text=True, check=True)
        arguments["verify_cases"](client, [case])

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
