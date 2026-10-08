"""Verify correlated ORDER BY alias scopes on native MySQL 8.4.11."""

import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ('CREATE TABLE ordering_scope(v INT,w INT);INSERT INTO ordering_scope VALUES(2,10),(1,20)', ''),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT a)', 'a\n-2\n-1\n'),
    ('SELECT -v AS v FROM ordering_scope ORDER BY (SELECT v)', 'v\n-1\n-2\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT a+0)', 'a\n-2\n-1\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT (SELECT a))', 'a\n-2\n-1\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT a FROM (SELECT 3 AS a) t)', 'a\n-2\n-1\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT a FROM (SELECT 3 AS x) t)', 'a\n-2\n-1\n'),
    ('SELECT v AS a,w AS a FROM ordering_scope ORDER BY (SELECT a)', (1052, '23000')),
    ('SELECT v AS a,-v AS a FROM ordering_scope ORDER BY (SELECT a)', 'a\ta\n2\t-2\n1\t-1\n'),
    ('SELECT SUM(-v) AS a FROM ordering_scope GROUP BY v ORDER BY (SELECT a)', (1247, '42S22')),
    ('SELECT ROW_NUMBER() OVER (ORDER BY v DESC) AS a FROM ordering_scope ORDER BY (SELECT a)', (3594, 'HY000')),
    ('SELECT -v AS a FROM ordering_scope WHERE FALSE ORDER BY (SELECT a)', ''),
    ('SET @n=0;SELECT (@n:=@n+1) AS a FROM ordering_scope ORDER BY (SELECT a);SELECT @n', 'a\n1\n2\n@n\n2\n'),
    ('SET @n=0;SELECT (@n:=@n+1) AS a FROM ordering_scope ORDER BY (SELECT a+0);SELECT @n', 'a\n1\n2\n@n\n2\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY EXISTS(SELECT a)', 'a\n-2\n-1\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT 1 WHERE a=-1)', 'a\n-2\n-1\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT ordering_scope.a)', (1054, '42S22')),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT a FROM ordering_scope inner_scope LIMIT 1)', 'a\n-2\n-1\n'),
]

prepared_cases = [
    ("PREPARE scoped_order FROM '" + sql.replace("'", "''") + "'", expected if isinstance(expected, tuple) else "")
    for sql, expected in cases
    if sql.startswith("SELECT ")
]


def verify(client, _writer):
    arguments["verify_cases"](client, cases + prepared_cases)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
