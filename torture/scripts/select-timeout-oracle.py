"""Pin SELECT timeout settings and interruption on native MySQL 8.4.11."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ('SET max_execution_time=1;SELECT SLEEP(0.1),1', 'SLEEP(0.1)\t1\n1\t1\n'),
    ('CREATE TABLE timeout_rows(n INT);INSERT INTO timeout_rows VALUES(1),(2);SET max_execution_time=1;SELECT n,SLEEP(0.1) FROM timeout_rows', (3024, 'HY000')),
    ('SET max_execution_time=1;SELECT COUNT(*) FROM timeout_rows a JOIN timeout_rows b ON SLEEP(0.1)=0', (3024, 'HY000')),
    ('SET max_execution_time=18446744073709551615;SELECT @@max_execution_time;SHOW WARNINGS', '@@max_execution_time\n18446744073709551615\n'),
    ("SET max_execution_time=1;PREPARE s FROM 'SELECT n,SLEEP(0.1) FROM timeout_rows';EXECUTE s", (3024, 'HY000')),
    ("SET max_execution_time=0;PREPARE s FROM 'SELECT n,SLEEP(0.1) FROM timeout_rows';SET max_execution_time=1;EXECUTE s", (3024, 'HY000')),
    ('SET max_execution_time=1;UPDATE timeout_rows SET n=n+SLEEP(0.05);SELECT n FROM timeout_rows ORDER BY n', 'n\n1\n2\n'),
    ('SELECT @@session.max_execution_time,@@global.max_execution_time', '@@session.max_execution_time\t@@global.max_execution_time\n0\t0\n'),
    ('SET max_execution_time=1;SELECT @@max_execution_time', '@@max_execution_time\n1\n'),
    ('SET max_execution_time=-1;SHOW WARNINGS;SELECT @@max_execution_time', "Level\tCode\tMessage\nWarning\t1292\tTruncated incorrect max_execution_time value: '-1'\n@@max_execution_time\n0\n"),
    ('SET max_execution_time=1.5', (1232, '42000')),
    ("SET max_execution_time='2'", (1232, '42000')),
    ('SET max_execution_time=NULL', (1232, '42000')),
    ('SET max_execution_time=4294967296;SHOW WARNINGS;SELECT @@max_execution_time', '@@max_execution_time\n4294967296\n'),
    ('SET max_execution_time=1;SELECT SLEEP(0.1) AS slept;SHOW WARNINGS', 'slept\n1\n'),
    ("SET max_execution_time=1;SELECT BENCHMARK(10000000,SHA2('abc',256)) AS n", 'n\n0\n'),
    ("SET max_execution_time=0;SELECT /*+ MAX_EXECUTION_TIME(1) */ BENCHMARK(10000000,SHA2('abc',256)) AS n", 'n\n0\n'),
]

def verify(client, _writer):
    arguments["verify_cases"](client, cases)

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
