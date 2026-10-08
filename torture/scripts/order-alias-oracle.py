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


selection_cases = [
    ('SELECT missing,v AS a,w AS a FROM duplicate_values ORDER BY a', (1054, '42S22')),
    ('SELECT v AS a,w AS a FROM duplicate_values WHERE missing ORDER BY a', (1054, '42S22')),
    ('SELECT v AS a,ROW_NUMBER() OVER (ORDER BY v DESC) AS a FROM duplicate_values ORDER BY a', 'a\ta\n2\t1\n1\t2\n'),
    ('SELECT SUM(-v) AS a,ROW_NUMBER() OVER (ORDER BY v) AS a FROM duplicate_values GROUP BY v ORDER BY a', 'a\ta\n-2\t2\n-1\t1\n'),
    ('SELECT SUM(-v) AS a,ROW_NUMBER() OVER (ORDER BY v) AS a FROM duplicate_values GROUP BY v ORDER BY ABS(a+3)', 'a\ta\n-2\t2\n-1\t1\n'),
    ('SELECT v AS a,w AS a,-v AS a FROM duplicate_values ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,w AS a,-v AS a FROM duplicate_values ORDER BY ABS(a+3)', (1052, '23000')),
    ('SELECT v AS a,-v AS a,w AS a FROM duplicate_values ORDER BY a', 'a\ta\ta\n2\t-2\t10\n1\t-1\t20\n'),
    ('SELECT v AS a,-v AS a,w AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\ta\n2\t-2\t10\n1\t-1\t20\n'),
    ('SELECT -v AS a,v AS a,w AS a FROM duplicate_values ORDER BY a', 'a\ta\ta\n-2\t2\t10\n-1\t1\t20\n'),
    ('SELECT -v AS a,v AS a,w AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\ta\n-2\t2\t10\n-1\t1\t20\n'),
    ('SELECT v AS a,v AS a,w AS a FROM duplicate_values ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,v AS a,w AS a FROM duplicate_values ORDER BY ABS(a+3)', (1052, '23000')),
    ('SELECT v AS a,duplicate_values.v AS a FROM duplicate_values ORDER BY a', 'a\ta\n1\t1\n2\t2\n'),
    ('SELECT v AS a,duplicate_values.v AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\n1\t1\n2\t2\n'),
    ('SELECT duplicate_values.v AS a,v AS a FROM duplicate_values ORDER BY a', 'a\ta\n1\t1\n2\t2\n'),
    ('SELECT duplicate_values.v AS a,v AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\n1\t1\n2\t2\n'),
    ('SELECT v AS a,(v) AS a FROM duplicate_values ORDER BY a', 'a\ta\n1\t1\n2\t2\n'),
    ('SELECT v AS a,(v) AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\n1\t1\n2\t2\n'),
    ('SELECT v AS a,+v AS a FROM duplicate_values ORDER BY a', 'a\ta\n1\t1\n2\t2\n'),
    ('SELECT v AS a,+v AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\n1\t1\n2\t2\n'),
    ('SELECT v AS a,CAST(v AS SIGNED) AS a FROM duplicate_values ORDER BY a', 'a\ta\n1\t1\n2\t2\n'),
    ('SELECT v AS a,CAST(v AS SIGNED) AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\n1\t1\n2\t2\n'),
    ('SELECT v AS a,w+0 AS a,-v AS a FROM duplicate_values ORDER BY a', 'a\ta\ta\n2\t10\t-2\n1\t20\t-1\n'),
    ('SELECT v AS a,w+0 AS a,-v AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\ta\n2\t10\t-2\n1\t20\t-1\n'),
    ('SELECT *,-v AS v FROM duplicate_values ORDER BY v', 'v\tw\tv\n2\t10\t-2\n1\t20\t-1\n'),
    ('SELECT *,-v AS v FROM duplicate_values ORDER BY ABS(v+3)', 'v\tw\tv\n1\t20\t-1\n2\t10\t-2\n'),
    ('SELECT *,w AS v FROM duplicate_values ORDER BY v', (1052, '23000')),
    ('SELECT *,w AS v FROM duplicate_values ORDER BY ABS(v+3)', 'v\tw\tv\n1\t20\t20\n2\t10\t10\n'),
    ('SELECT v AS a,v+0 AS a,w AS a FROM duplicate_values ORDER BY a', 'a\ta\ta\n1\t1\t20\n2\t2\t10\n'),
    ('SELECT v AS a,v+0 AS a,w AS a FROM duplicate_values ORDER BY ABS(a+3)', 'a\ta\ta\n1\t1\t20\n2\t2\t10\n'),
    ('SELECT l.v AS a,r.v AS a FROM duplicate_values l JOIN duplicate_values r ON l.v=r.v ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,w AS a FROM duplicate_values WHERE FALSE ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,v AS a FROM duplicate_values GROUP BY v ORDER BY ABS(a)', 'a\ta\n1\t1\n2\t2\n'),
    ('SELECT SUM(v) AS a,SUM(w) AS a FROM duplicate_values GROUP BY v,w ORDER BY ABS(a)', 'a\ta\n1\t20\n2\t10\n'),
    ('CREATE VIEW duplicate_view AS SELECT v,w FROM duplicate_values', ''),
    ('CREATE VIEW duplicate_constants AS SELECT 1 AS v,2 AS w', ''),
    ('SELECT v AS a,w AS a FROM (SELECT v,w FROM duplicate_values) t ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,w AS a,-v AS a FROM (SELECT v,w FROM duplicate_values) t ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,-v AS a,w AS a FROM (SELECT v,w FROM duplicate_values) t ORDER BY a', 'a\ta\ta\n2\t-2\t10\n1\t-1\t20\n'),
    ('SELECT v AS a,w AS a FROM (SELECT v,w FROM duplicate_values LIMIT 10) t ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,w AS a,-v AS a FROM (SELECT v,w FROM duplicate_values LIMIT 10) t ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,-v AS a,w AS a FROM (SELECT v,w FROM duplicate_values LIMIT 10) t ORDER BY a', 'a\ta\ta\n2\t-2\t10\n1\t-1\t20\n'),
    ('SELECT v AS a,w AS a FROM (SELECT 1 AS v,2 AS w) t ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,w AS a,-v AS a FROM (SELECT 1 AS v,2 AS w) t ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,-v AS a,w AS a FROM (SELECT 1 AS v,2 AS w) t ORDER BY a', 'a\ta\ta\n1\t-1\t2\n'),
    ('SELECT v AS a,w AS a FROM duplicate_view ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,w AS a,-v AS a FROM duplicate_view ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,-v AS a,w AS a FROM duplicate_view ORDER BY a', 'a\ta\ta\n2\t-2\t10\n1\t-1\t20\n'),
    ('SELECT v AS a,w AS a FROM duplicate_constants ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,w AS a,-v AS a FROM duplicate_constants ORDER BY a', (1052, '23000')),
    ('SELECT v AS a,-v AS a,w AS a FROM duplicate_constants ORDER BY a', 'a\ta\ta\n1\t-1\t2\n'),
    ('SELECT v AS a,+w AS a,-v AS a FROM duplicate_values ORDER BY a', (1052, '23000')),
    ('CREATE VIEW duplicate_same AS SELECT v AS x,v AS y FROM duplicate_values', ''),
    ('SELECT x AS a,y AS a FROM duplicate_same ORDER BY a', (1052, '23000')),
    ('SELECT x AS a,y AS a FROM (SELECT v AS x,v AS y FROM duplicate_values) t ORDER BY a', (1052, '23000')),
    ('SELECT x AS a,y AS a FROM (SELECT v AS x,v AS y FROM duplicate_values LIMIT 10) t ORDER BY a', (1052, '23000')),
]

# Preparation resolves duplicate names before any source rows are read.
prepared_selection_cases = [
    ("PREPARE selection FROM '" + sql.replace("'", "''") + "'", expected if isinstance(expected, tuple) else "")
    for sql, expected in selection_cases
    if sql.startswith("SELECT ")
]


def verify(client, _writer):
    arguments["verify_cases"](client, cases + selection_cases + prepared_selection_cases)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
