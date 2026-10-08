"""Verify ORDER BY alias precedence and evaluation on native MySQL 8.4.11."""

import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ('CREATE TABLE ordering_values(v INT);INSERT INTO ordering_values VALUES(2),(1)', ''),
    ('SELECT -v AS v FROM ordering_values ORDER BY v+0', 'v\n-1\n-2\n'),
    ('SELECT -v AS v FROM ordering_values ORDER BY v', 'v\n-2\n-1\n'),
    ('SELECT v AS a,-v AS a FROM ordering_values ORDER BY ABS(a+3)', 'a\ta\n2\t-2\n1\t-1\n'),
    ('SELECT v AS a,-v AS a FROM ordering_values ORDER BY a', 'a\ta\n2\t-2\n1\t-1\n'),
    ('SET @n=0;SELECT (@n:=@n+1) AS n FROM ordering_values ORDER BY n;SELECT @n', 'n\n1\n2\n@n\n2\n'),
    ('SET @n=0;SELECT (@n:=@n+1) AS n FROM ordering_values ORDER BY n+0;SELECT @n', 'n\n1\n3\n@n\n4\n'),
    ('SELECT SUM(v) AS s FROM ordering_values ORDER BY ABS(s)', 's\n3\n'),
    ('SELECT v AS a FROM ordering_values ORDER BY (SELECT a)', 'a\n1\n2\n'),
    ('SELECT v AS a FROM ordering_values ORDER BY ABS(a)', 'a\n1\n2\n'),
    ('SELECT v AS a FROM ordering_values WHERE FALSE ORDER BY ABS(a)', ''),
    ('SELECT v AS a FROM ordering_values ORDER BY ABS(missing)', (1054, '42S22')),
    ('SELECT v AS a FROM ordering_values GROUP BY v ORDER BY ABS(a)', 'a\n1\n2\n'),
    ('CREATE TABLE duplicate_values(v INT,w INT);INSERT INTO duplicate_values VALUES(2,10),(1,20)', ''),
    ('SELECT v AS a,w AS a FROM duplicate_values ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,w AS a FROM duplicate_values ORDER BY ABS(a+3)', (1052, '23000')),
    ('SELECT v+0 AS a,w+0 AS a FROM duplicate_values ORDER BY a', 'a\ta\n1\t20\n2\t10\n'),
    ('SELECT v+0 AS a,w+0 AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\n1\t20\n2\t10\n'),
    ('SELECT v AS a,v AS a FROM duplicate_values ORDER BY a', 'a\ta\n1\t1\n2\t2\n'),
    ('SELECT v AS a,v AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\n1\t1\n2\t2\n'),
    ('SELECT -v AS a,v AS a FROM duplicate_values ORDER BY a', 'a\ta\n-2\t2\n-1\t1\n'),
    ('SELECT -v AS a,v AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\n-2\t2\n-1\t1\n'),
    ('SELECT -v AS a,v+0 AS a FROM duplicate_values ORDER BY a', 'a\ta\n-2\t2\n-1\t1\n'),
    ('SELECT -v AS a,v+0 AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\n-2\t2\n-1\t1\n'),
    ('SELECT v+0 AS a,-v AS a FROM duplicate_values ORDER BY a', 'a\ta\n1\t-1\n2\t-2\n'),
    ('SELECT v+0 AS a,-v AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\n1\t-1\n2\t-2\n'),
    ('SELECT 1 AS a,2 AS a FROM duplicate_values ORDER BY a', 'a\ta\n1\t2\n1\t2\n'),
    ('SELECT 1 AS a,2 AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\n1\t2\n1\t2\n'),
    ('SELECT v AS a FROM duplicate_values ORDER BY COERCIBILITY(a)', 'a\n2\n1\n'),
    ('SELECT v AS a FROM duplicate_values ORDER BY a COLLATE utf8mb4_bin', 'a\n1\n2\n'),
    ('SELECT ROW_NUMBER() OVER (ORDER BY v) AS r FROM duplicate_values ORDER BY ABS(r)', 'r\n1\n2\n'),
    ('SELECT v AS a FROM duplicate_values ORDER BY (SELECT a)', 'a\n1\n2\n'),
    ('SELECT v AS a FROM ordering_values ORDER BY SUM(a)', (3029, 'HY000')),
    ('SELECT v AS a FROM ordering_values ORDER BY ROW_NUMBER() OVER (ORDER BY a)', (1054, '42S22')),
    ('SELECT v AS a FROM ordering_values ORDER BY ABS(a)+0', 'a\n1\n2\n'),
    ('SELECT v+1 FROM ordering_values ORDER BY ABS(`v+1`)', 'v+1\n2\n3\n'),
    ('SELECT SUM(v) AS s FROM ordering_values ORDER BY SUM(s)', (1111, 'HY000')),
    ('SELECT v AS a FROM ordering_values ORDER BY ABS(a) LIMIT 1', 'a\n1\n'),
    ('SELECT v AS a FROM ordering_values ORDER BY ABS(a) DESC', 'a\n2\n1\n'),
    ('SET @n=0;SELECT SUM(@n:=@n+1) AS s FROM ordering_values ORDER BY ABS(s);SELECT @n', 's\n3\n@n\n2\n'),
    ('SET @n=0;SELECT SUM(@n:=@n+1) AS s FROM ordering_values ORDER BY ABS(SUM(@n:=@n+1));SELECT @n', 's\n3\n@n\n2\n'),
    ('SET @n=0;SELECT SUM(v)+(@n:=@n+1) AS s FROM ordering_values ORDER BY ABS(s);SELECT @n', 's\n4\n@n\n1\n'),
    ('SET @n=0;SELECT SUM(@n:=@n+1) AS s FROM ordering_values GROUP BY v ORDER BY ABS(s);SELECT @n', 's\n1\n2\n@n\n2\n'),
    ('SET @n=0;SELECT SUM(@n:=@n+1) AS s FROM ordering_values GROUP BY v ORDER BY ABS(SUM(@n:=@n+1));SELECT @n', 's\n2\n4\n@n\n4\n'),
    ('SET @n=0;SELECT SUM(v)+(@n:=@n+1) AS s FROM ordering_values GROUP BY v ORDER BY ABS(s);SELECT @n', 's\n3\n3\n@n\n2\n'),
    ('SET @n=0;SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM ordering_values GROUP BY v ORDER BY IF(a=b,v,-v);SELECT @n', 'a\tb\tv\n1\t2\t2\n5\t6\t1\n@n\n6\n'),
    ("SELECT _latin1'a' AS s FROM ordering_values GROUP BY v ORDER BY s COLLATE utf8mb4_bin", (1253, '42000')),
    ('SELECT v AS a FROM ordering_values GROUP BY v ORDER BY SUM(a)', 'a\n1\n2\n'),
    ('SELECT SUM(v) AS s FROM ordering_values GROUP BY v ORDER BY COERCIBILITY(SUM(s))', (1111, 'HY000')),
]


def verify(client, _writer):
    arguments["verify_cases"](client, cases)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
