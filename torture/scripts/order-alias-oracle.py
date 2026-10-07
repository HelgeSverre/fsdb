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
]


def verify(client, _writer):
    arguments["verify_cases"](client, cases)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
