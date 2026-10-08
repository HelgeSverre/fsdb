"""Pin SET_VAR contextualization and warning lifetimes on native MySQL 8.4.11."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [('SELECT /*+ SET_VAR(max_points_in_geometry=9) */ @@max_points_in_geometry AS n, (SELECT /*+ '
  'SET_VAR(max_points_in_geometry=7) */ @@max_points_in_geometry) AS child;SHOW WARNINGS',
  'n\tchild\n'
  '7\t7\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ SET_VAR(unknown_outer=1) */ (SELECT /*+ SET_VAR(unknown_inner=1) */ 1) AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t3128\tUnresolved name 'unknown_inner' for SET_VAR hint\n"
  "Warning\t3128\tUnresolved name 'unknown_outer' for SET_VAR hint\n"),
 ('SELECT /*+ SET_VAR(max_points_in_geometry=9) */ (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1) AS '
  'n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ SET_VAR(max_points_in_geometry=9) SET_VAR(max_points_in_geometry=8) */ (SELECT /*+ '
  'SET_VAR(max_points_in_geometry=7) */ 1) AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=8)  is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ SET_VAR(max_points_in_geometry=9) */ 1 AS n UNION ALL SELECT /*+ '
  'SET_VAR(max_points_in_geometry=7) */ 2 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  '2\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=7)  is ignored as conflicting/duplicated\n'),
 ('WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) SELECT /*+ '
  'SET_VAR(max_points_in_geometry=9) */ n FROM c;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ SET_VAR(max_points_in_geometry=2) */ (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1) AS '
  'n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=2)  is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ SET_VAR(max_points_in_geometry=9) */ (SELECT /*+ SET_VAR(max_points_in_geometry=2) */ 1) AS '
  'n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'
  "Warning\t1292\tTruncated incorrect max_points_in_geometry value: '2'\n"),
 ('SELECT /*+ SET_VAR(max_points_in_geometry=9) QB_NAME(q) QB_NAME(r) */ (SELECT /*+ '
  'SET_VAR(max_points_in_geometry=7) QB_NAME(q) QB_NAME(r) */ 1) AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`r`) is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ SET_VAR(unknown_outer=1) */ (SELECT /*+ BKA(x) */ 1) AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t3128\tUnresolved name 'unknown_outer' for SET_VAR hint\n"
  'Warning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'),
 ('SELECT /*+ BKA(x) */ (SELECT /*+ SET_VAR(unknown_inner=1) */ 1) AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t3128\tUnresolved name 'unknown_inner' for SET_VAR hint\n"
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ("SELECT /*+ SET_VAR(max_points_in_geometry=9) */ (SELECT /*+ SET_VAR(max_points_in_geometry='bad') */ 1) "
  'AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'
  "Warning\t1232\tIncorrect argument type to variable 'max_points_in_geometry'\n"),
 ("SELECT /*+ SET_VAR(max_points_in_geometry='bad') SET_VAR(max_points_in_geometry=9) */ "
  '@@max_points_in_geometry AS n;SHOW WARNINGS',
  'n\n'
  '65536\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'
  "Warning\t1232\tIncorrect argument type to variable 'max_points_in_geometry'\n"),
 ('SELECT /*+ SET_VAR(max_points_in_geometry=2) SET_VAR(max_points_in_geometry=9) */ '
  '@@max_points_in_geometry AS n;SHOW WARNINGS',
  'n\n'
  '3\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'
  "Warning\t1292\tTruncated incorrect max_points_in_geometry value: '2'\n"),
 ('SELECT /*+ SET_VAR(unknown=1) SET_VAR(unknown=2) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t3128\tUnresolved name 'unknown' for SET_VAR hint\n"
  "Warning\t3128\tUnresolved name 'unknown' for SET_VAR hint\n"),
 ("PREPARE s FROM 'SELECT /*+ SET_VAR(max_points_in_geometry=2) SET_VAR(max_points_in_geometry=9) */ "
  "@@max_points_in_geometry AS n';SHOW WARNINGS;EXECUTE s;SHOW WARNINGS",
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SET_VAR(max_points_in_geometry=9)  is ignored as conflicting/duplicated\n'
  "Warning\t1292\tTruncated incorrect max_points_in_geometry value: '2'\n"
  'n\n'
  '3\n'
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect max_points_in_geometry value: '2'\n"),
 ("PREPARE s FROM 'SELECT /*+ SET_VAR(unknown=1) */ 1 AS n';SHOW WARNINGS;EXECUTE s;SHOW WARNINGS",
  "Level\tCode\tMessage\nWarning\t3128\tUnresolved name 'unknown' for SET_VAR hint\nn\n1\n"),
 ('SELECT /*+ SET_VAR(max_points_in_geometry=9) */ (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1) AS '
  'n;SELECT @@max_points_in_geometry AS restored;SHOW WARNINGS',
  'n\n1\nrestored\n65536\n')]

def verify(client, _writer):
    arguments["verify_cases"](client, cases)

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
