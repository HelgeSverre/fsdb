"""Pin native non-strict and IGNORE mutation conversion diagnostics."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
setup = "DROP TABLE IF EXISTS predicate_base;CREATE TABLE predicate_base(n INT,s VARCHAR(20));INSERT INTO predicate_base VALUES(1,'x'),(2,'1x'),(3,'0x');"
cases = [("SET sql_mode='';UPDATE predicate_base SET n=n+10 WHERE 'x';SHOW WARNINGS;SELECT n,s FROM "
  'predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: 'x'\n"
  'n\ts\n'
  '1\tx\n'
  '2\t1x\n'
  '3\t0x\n'),
 ("SET sql_mode='';UPDATE predicate_base SET n=n+10 WHERE '1x';SHOW WARNINGS;SELECT n,s FROM "
  'predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  'n\ts\n'
  '11\tx\n'
  '12\t1x\n'
  '13\t0x\n'),
 ("SET sql_mode='';UPDATE predicate_base SET n=n+10 WHERE s;SHOW WARNINGS;SELECT n,s FROM "
  'predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: 'x'\n"
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  "Warning\t1292\tTruncated incorrect DOUBLE value: '0x'\n"
  'n\ts\n'
  '1\tx\n'
  '3\t0x\n'
  '12\t1x\n'),
 ("SET sql_mode='';UPDATE predicate_base SET n=n+10 WHERE n=1 OR s;SHOW WARNINGS;SELECT n,s FROM "
  'predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  "Warning\t1292\tTruncated incorrect DOUBLE value: '0x'\n"
  'n\ts\n'
  '3\t0x\n'
  '11\tx\n'
  '12\t1x\n'),
 ("SET sql_mode='';UPDATE predicate_base SET n=n+10 WHERE 'x' LIMIT 0;SHOW WARNINGS;SELECT n,s "
  'FROM predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: 'x'\n"
  'n\ts\n'
  '1\tx\n'
  '2\t1x\n'
  '3\t0x\n'),
 ("SET sql_mode='';UPDATE predicate_base SET n=n+10 WHERE '1x' LIMIT 1;SHOW WARNINGS;SELECT n,s "
  'FROM predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  'n\ts\n'
  '2\t1x\n'
  '3\t0x\n'
  '11\tx\n'),
 ("SET sql_mode='';DELETE FROM predicate_base WHERE 'x';SHOW WARNINGS;SELECT n,s FROM "
  'predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: 'x'\n"
  'n\ts\n'
  '1\tx\n'
  '2\t1x\n'
  '3\t0x\n'),
 ("SET sql_mode='';DELETE FROM predicate_base WHERE '1x';SHOW WARNINGS;SELECT n,s FROM "
  'predicate_base ORDER BY n',
  "Level\tCode\tMessage\nWarning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"),
 ("SET sql_mode='';DELETE FROM predicate_base WHERE s;SHOW WARNINGS;SELECT n,s FROM predicate_base "
  'ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: 'x'\n"
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  "Warning\t1292\tTruncated incorrect DOUBLE value: '0x'\n"
  'n\ts\n'
  '1\tx\n'
  '3\t0x\n'),
 ("SET sql_mode='';DELETE FROM predicate_base WHERE n=1 OR s;SHOW WARNINGS;SELECT n,s FROM "
  'predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  "Warning\t1292\tTruncated incorrect DOUBLE value: '0x'\n"
  'n\ts\n'
  '3\t0x\n'),
 ("SET sql_mode='';DELETE FROM predicate_base WHERE 'x' LIMIT 0;SHOW WARNINGS;SELECT n,s FROM "
  'predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: 'x'\n"
  'n\ts\n'
  '1\tx\n'
  '2\t1x\n'
  '3\t0x\n'),
 ("SET sql_mode='';DELETE FROM predicate_base WHERE '1x' LIMIT 1;SHOW WARNINGS;SELECT n,s FROM "
  'predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  'n\ts\n'
  '2\t1x\n'
  '3\t0x\n'),
 ("UPDATE IGNORE predicate_base SET n=n+10 WHERE 'x';SHOW WARNINGS;SELECT n,s FROM predicate_base "
  'ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: 'x'\n"
  'n\ts\n'
  '1\tx\n'
  '2\t1x\n'
  '3\t0x\n'),
 ("UPDATE IGNORE predicate_base SET n=n+10 WHERE '1x';SHOW WARNINGS;SELECT n,s FROM predicate_base "
  'ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  'n\ts\n'
  '11\tx\n'
  '12\t1x\n'
  '13\t0x\n'),
 ('UPDATE IGNORE predicate_base SET n=n+10 WHERE s;SHOW WARNINGS;SELECT n,s FROM predicate_base '
  'ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: 'x'\n"
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  "Warning\t1292\tTruncated incorrect DOUBLE value: '0x'\n"
  'n\ts\n'
  '1\tx\n'
  '3\t0x\n'
  '12\t1x\n'),
 ('UPDATE IGNORE predicate_base SET n=n+10 WHERE n=1 OR s;SHOW WARNINGS;SELECT n,s FROM '
  'predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  "Warning\t1292\tTruncated incorrect DOUBLE value: '0x'\n"
  'n\ts\n'
  '3\t0x\n'
  '11\tx\n'
  '12\t1x\n'),
 ("UPDATE IGNORE predicate_base SET n=n+10 WHERE 'x' LIMIT 0;SHOW WARNINGS;SELECT n,s FROM "
  'predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: 'x'\n"
  'n\ts\n'
  '1\tx\n'
  '2\t1x\n'
  '3\t0x\n'),
 ("UPDATE IGNORE predicate_base SET n=n+10 WHERE '1x' LIMIT 1;SHOW WARNINGS;SELECT n,s FROM "
  'predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  'n\ts\n'
  '2\t1x\n'
  '3\t0x\n'
  '11\tx\n'),
 ("DELETE IGNORE FROM predicate_base WHERE 'x';SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER "
  'BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: 'x'\n"
  'n\ts\n'
  '1\tx\n'
  '2\t1x\n'
  '3\t0x\n'),
 ("DELETE IGNORE FROM predicate_base WHERE '1x';SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER "
  'BY n',
  "Level\tCode\tMessage\nWarning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"),
 ('DELETE IGNORE FROM predicate_base WHERE s;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY '
  'n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: 'x'\n"
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  "Warning\t1292\tTruncated incorrect DOUBLE value: '0x'\n"
  'n\ts\n'
  '1\tx\n'
  '3\t0x\n'),
 ('DELETE IGNORE FROM predicate_base WHERE n=1 OR s;SHOW WARNINGS;SELECT n,s FROM predicate_base '
  'ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  "Warning\t1292\tTruncated incorrect DOUBLE value: '0x'\n"
  'n\ts\n'
  '3\t0x\n'),
 ("DELETE IGNORE FROM predicate_base WHERE 'x' LIMIT 0;SHOW WARNINGS;SELECT n,s FROM "
  'predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: 'x'\n"
  'n\ts\n'
  '1\tx\n'
  '2\t1x\n'
  '3\t0x\n'),
 ("DELETE IGNORE FROM predicate_base WHERE '1x' LIMIT 1;SHOW WARNINGS;SELECT n,s FROM "
  'predicate_base ORDER BY n',
  'Level\tCode\tMessage\n'
  "Warning\t1292\tTruncated incorrect DOUBLE value: '1x'\n"
  'n\ts\n'
  '2\t1x\n'
  '3\t0x\n')]

def verify(client, _writer):
    arguments["verify_cases"](client, [(setup + sql, expected) for sql, expected in cases])

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
