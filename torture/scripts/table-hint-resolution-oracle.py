"""Pin table, index, and query-block hint diagnostics on native MySQL 8.4.11."""
import pathlib
import runpy
import subprocess

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [('SELECT /*+ BKA(t) */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`select#1` for BKA hint\n'),
 ('SELECT /*+ QB_NAME(q) BKA(t) */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`q` for BKA hint\n'),
 ('SELECT /*+ BKA(t@q) */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3127\tQuery block name `q` is not found for BKA hint\n'),
 ('SELECT /*+ BKA(@q t) */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3127\tQuery block name `q` is not found for BKA hint\n'),
 ('SELECT /*+ QB_NAME(q) BKA(t@q) */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`q` for BKA hint\n'),
 ('SELECT /*+ BKA(z,t) NO_INDEX(z) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `z`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `z`@`select#1` for NO_INDEX hint\n'),
 ('SELECT /*+ BKA(t) */ 1 AS n FROM t AS a;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`select#1` for BKA hint\n'),
 ('SELECT /*+ BKA(a) */ 1 AS n FROM t AS a;SHOW WARNINGS', ''),
 ('SELECT /*+ BKA(A) */ 1 AS n FROM t AS a;SHOW WARNINGS', ''),
 ('SELECT /*+ BKA(a) NO_BKA(a) */ 1 AS n FROM t AS a;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint NO_BKA(`a` ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ BKA(t) BKA(t) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint BKA(`t` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `t`@`select#1` for BKA hint\n'),
 ('SELECT /*+ BKA(t) NO_BKA(t) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_BKA(`t` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `t`@`select#1` for BKA hint\n'),
 ('SELECT /*+ NO_INDEX(t absent) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`select#1` `absent` for NO_INDEX hint\n'),
 ('SELECT /*+ BKA(a) */ 1 AS n FROM (SELECT 1) a;SHOW WARNINGS', 'n\n1\n'),
 ('SELECT (SELECT /*+ BKA(t) */ 1) AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`select#2` for BKA hint\n'),
 ('SELECT /*+ BKA(t) */ 1 AS n UNION ALL SELECT /*+ BKA(t) */ 2;SHOW WARNINGS',
  'n\n'
  '1\n'
  '2\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `t`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `t`@`select#2` for BKA hint\n'),
 ('SELECT /*+ BKA(t) */ 1 AS n FROM absent;SHOW WARNINGS', (1146, '42S02')),
 ('SELECT /*+ BKA(t) BOGUS */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n' at line 1\n"
  'Warning\t3128\tUnresolved name `t`@`select#1` for BKA hint\n'),
 ('SELECT /*+ BKA(t) SET_VAR(max_points_in_geometry=2) MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) */ 1 AS '
  'n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\n'
  "Warning\t1292\tTruncated incorrect max_points_in_geometry value: '2'\n"
  'Warning\t3128\tUnresolved name `t`@`select#1` for BKA hint\n'),
 ("PREPARE s FROM 'SELECT /*+ BKA(t) */ 1 AS n';SHOW WARNINGS;EXECUTE s;SHOW WARNINGS",
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`select#1` for BKA hint\nn\n1\n'),
 ('WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT /*+ BKA(y) */ * FROM c;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'),
 ('SELECT /*+ BKA(x) */ (SELECT /*+ BKA(y) */ 1) AS n FROM (SELECT /*+ BKA(z) */ 1) a;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `z`@`select#3` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#2` for BKA hint\n'),
 ('SELECT /*+ BKA(t@q) */ 1 AS n FROM (SELECT /*+ QB_NAME(q) */ 1 FROM t) a;SHOW WARNINGS', ''),
 ('SELECT /*+ QB_NAME(q) QB_NAME(r) BKA(t) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`r`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `t`@`q` for BKA hint\n'),
 ('SELECT /*+ QB_NAME(q) */ (SELECT /*+ QB_NAME(q) BKA(t) */ 1) AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `t`@`q` for BKA hint\n'),
 ('SELECT /*+ BKA(@q) */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3127\tQuery block name `q` is not found for BKA hint\n'),
 ('SELECT /*+ BKA(z,a) BKA(y,a) */ 1 AS n FROM t a;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint BKA(`a` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `z`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'),
 ('SELECT /*+ BKA(z) NO_INDEX(z) BKA(y) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `z`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `z`@`select#1` for NO_INDEX hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'),
 ('SELECT /*+ NO_INDEX(t absent,missing) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `t`@`select#1` `absent` for NO_INDEX hint\n'
  'Warning\t3128\tUnresolved name `t`@`select#1` `missing` for NO_INDEX hint\n'),
 ('SELECT /*+ NO_INDEX(z absent) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `z`@`select#1` for NO_INDEX hint\n'
  'Warning\t3128\tUnresolved name `z`@`select#1` `absent` for NO_INDEX hint\n'),
 ('SELECT /*+ BKA(t@`select#1`) */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`select#1` for BKA hint\n'),
 ('SELECT /*+ BKA(t@q,t@q) */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3127\tQuery block name `q` is not found for BKA hint\n'),
 ('SELECT /*+ QB_NAME(Q) BKA(t@q) */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`Q` for BKA hint\n'),
 ('SELECT /*+ BKA(t) */ 1 AS n FROM t;SHOW WARNINGS', ''),
 ('SELECT /*+ BKA(t) */ 1 AS n FROM t AS t;SHOW WARNINGS', ''),
 ('CREATE VIEW v AS SELECT /*+ BKA(t) */ 1 AS n;SHOW WARNINGS', '')]


cases += [('SELECT (WITH c AS (SELECT /*+ BKA(c_missing) */ 1 AS n) SELECT /*+ BKA(q_missing) */ n FROM c) AS n;SHOW '
  'WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `q_missing`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `c_missing`@`select#3` for BKA hint\n'),
 ('SELECT * FROM (WITH c AS (SELECT /*+ BKA(c_missing) */ 1 AS n) SELECT /*+ BKA(q_missing) */ n FROM c) '
  'a;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `q_missing`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `c_missing`@`select#3` for BKA hint\n'),
 ('SELECT /*+ NO_INDEX(t i1) NO_INDEX(t i2) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint NO_INDEX(`t`  `i2`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ INDEX(t i1) NO_INDEX(t i2) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint NO_INDEX(`t`  `i2`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ NO_INDEX(t i1) NO_INDEX(t i1) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint NO_INDEX(`t`  `i1`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ NO_INDEX(t i1,i2) NO_INDEX(t i2) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint NO_INDEX(`t`  `i2`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ NO_INDEX(t) INDEX(t i1) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint INDEX(`t`  `i1`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ BKA() BKA() */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3126\tHint BKA( ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ BKA() NO_BKA() */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3126\tHint NO_BKA( ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) QB_NAME(q) QB_NAME(r) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint QB_NAME(`r`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ QB_NAME(q) QB_NAME(r) MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`r`) is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\n'),
 ('/*!80000 */ SELECT /*+ BKA(x) */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('SELECT /*+ BKA(t) */ 1 AS n FROM t;SHOW WARNINGS;SELECT 1;SHOW WARNINGS', '1\n1\n')]

cases += [('SELECT /*+ QB_NAME(q) QB_NAME(r) BKA(t@r) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`r`) is ignored as conflicting/duplicated\n'
  'Warning\t3127\tQuery block name `r` is not found for BKA hint\n'),
 ('SELECT /*+ QB_NAME(q) QB_NAME(r) BKA(t@q) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`r`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `t`@`q` for BKA hint\n'),
 ('SELECT /*+ QB_NAME(q) */ (SELECT /*+ QB_NAME(q) BKA(t@q) */ 1) AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `t`@`q` for BKA hint\n')]

def verify(client, _writer):
    setup = subprocess.run(
        [*client.process.args, "-e", "USE probe;CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"],
        capture_output=True, text=True, check=True,
    )
    assert not setup.stderr, setup.stderr
    arguments["verify_cases"](client, cases)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
