"""Verify aggregate ownership and ORDER BY rejection on native MySQL 8.4.11."""

import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ('CREATE TABLE aggregate_order(v INT);INSERT INTO aggregate_order VALUES(2),(1)', ''),
    ('SELECT (SELECT SUM(v)) AS total FROM aggregate_order', 'total\n3\n'),
    ('SELECT v FROM aggregate_order ORDER BY (SELECT SUM(i.v) FROM aggregate_order i)', 'v\n2\n1\n'),
    ('SELECT v FROM aggregate_order ORDER BY (SELECT SUM(1))', 'v\n2\n1\n'),
    ('SELECT v FROM aggregate_order ORDER BY (SELECT SUM(v+1))', (3029, 'HY000')),
    ('SELECT v FROM aggregate_order ORDER BY (SELECT SUM(i.v+aggregate_order.v) FROM aggregate_order i)', 'v\n1\n2\n'),
    ('SELECT v FROM aggregate_order ORDER BY (SELECT SUM(aggregate_order.v) FROM aggregate_order i LIMIT 1)', (3029, 'HY000')),
    ('SELECT SUM(v) AS s FROM aggregate_order ORDER BY (SELECT SUM(v))', 's\n3\n'),
    ('SELECT v FROM aggregate_order ORDER BY (SELECT SUM((SELECT v)))', (3029, 'HY000')),
    ('SELECT v FROM aggregate_order ORDER BY SUM(v)', (3029, 'HY000')),
    ('SELECT v AS a FROM aggregate_order ORDER BY SUM(a)', (3029, 'HY000')),
    ('SELECT 1 FROM aggregate_order ORDER BY SUM(v)', (3029, 'HY000')),
    ('SELECT 1 ORDER BY SUM(1)', (3029, 'HY000')),
    ('SELECT v FROM aggregate_order ORDER BY v,SUM(v)', (3029, 'HY000')),
    ('SELECT v FROM aggregate_order ORDER BY SUM(missing)', (1054, '42S22')),
    ('SELECT missing FROM aggregate_order ORDER BY SUM(v)', (1054, '42S22')),
    ('SELECT v FROM aggregate_order WHERE missing ORDER BY SUM(v)', (1054, '42S22')),
    ('SELECT v FROM aggregate_order WHERE FALSE ORDER BY SUM(v)', (3029, 'HY000')),
    ('SELECT v FROM aggregate_order ORDER BY SUM(v) LIMIT 0', (3029, 'HY000')),
    ('SELECT SUM(v) FROM aggregate_order ORDER BY SUM(v)', 'SUM(v)\n3\n'),
    ('SELECT v FROM aggregate_order GROUP BY v ORDER BY SUM(v)', 'v\n1\n2\n'),
    ('SELECT v FROM aggregate_order HAVING COUNT(*)>0 ORDER BY SUM(v)', (1140, '42000')),
    ('SELECT 1 FROM aggregate_order HAVING COUNT(*)>0 ORDER BY SUM(v)', '1\n1\n'),
    ('SELECT v FROM aggregate_order HAVING TRUE ORDER BY SUM(v)', (3029, 'HY000')),
    ('SELECT v FROM aggregate_order ORDER BY SUM(SUM(v))', (1111, 'HY000')),
    ('SELECT SUM(v) AS a FROM aggregate_order ORDER BY SUM(a)', (1111, 'HY000')),
    ('SELECT ROW_NUMBER() OVER (ORDER BY v) FROM aggregate_order ORDER BY SUM(v)', (3029, 'HY000')),
    ('SELECT v FROM aggregate_order ORDER BY (SELECT SUM(v))', (3029, 'HY000')),
    ('SELECT v FROM aggregate_order ORDER BY ABS(SUM(v))', (3029, 'HY000')),
    ('SELECT DISTINCT v FROM aggregate_order ORDER BY SUM(v)', (3029, 'HY000')),
    ("SET sql_mode='';SELECT v FROM aggregate_order ORDER BY SUM(v)", (3029, 'HY000')),
    ('SELECT (SELECT SUM(v)) AS total FROM aggregate_order WHERE FALSE', 'total\nNULL\n'),
    ('SELECT (SELECT SUM(v) WHERE FALSE) AS total FROM aggregate_order', 'total\nNULL\n'),
    ('SELECT (SELECT SUM(v) FROM aggregate_order i) AS total FROM aggregate_order', 'total\n3\n3\n'),
    ('SELECT v,(SELECT SUM(v)) AS total FROM aggregate_order GROUP BY v ORDER BY v', 'v\ttotal\n1\t1\n2\t2\n'),
    ('SELECT (SELECT (SELECT SUM(v))) AS total FROM aggregate_order', 'total\n3\n'),
    ('SELECT (SELECT COUNT(v)) AS total FROM aggregate_order', 'total\n2\n'),
    ('SELECT (SELECT v) AS value,(SELECT SUM(v)) AS total FROM aggregate_order', (1140, '42000')),
    ('SELECT (SELECT SUM(x)) AS total FROM (SELECT v AS x FROM aggregate_order) d', 'total\n3\n'),
    ('SELECT 3 IN (SELECT SUM(v)) AS hit FROM aggregate_order', 'hit\n1\n'),
    ('SELECT 3=ANY(SELECT SUM(v)) AS hit FROM aggregate_order', 'hit\n1\n'),
    ('SELECT EXISTS(SELECT SUM(v)) AS hit FROM aggregate_order', 'hit\n1\n'),
    ('SELECT (SELECT SUM(aggregate_order.v) FROM aggregate_order i LIMIT 1) AS total FROM aggregate_order', 'total\n3\n'),
    ('SELECT (SELECT SUM(aggregate_order.v) FROM aggregate_order i) AS total FROM aggregate_order', (1242, '21000')),
    ('SELECT ANY_VALUE((SELECT v)) AS value,(SELECT SUM(v)) AS total FROM aggregate_order', 'value\ttotal\n2\t3\n'),
    ('SELECT (SELECT ANY_VALUE(v)) AS value,(SELECT SUM(v)) AS total FROM aggregate_order', 'value\ttotal\n2\t3\n'),
    ('SELECT (SELECT SUM(v)) AS total,ROW_NUMBER() OVER () AS rn FROM aggregate_order', 'total\trn\n3\t1\n'),
    ('SELECT v,(SELECT SUM(v)) AS total,ROW_NUMBER() OVER (ORDER BY v) AS rn FROM aggregate_order GROUP BY v ORDER BY v', 'v\ttotal\trn\n1\t1\t1\n2\t2\t2\n'),
]

prepared_cases = [
    ("PREPARE aggregate_scope FROM '" + sql.replace("'", "''") + "'", expected if isinstance(expected, tuple) and expected[0] != 1242 else "")
    for sql, expected in cases
    if sql.startswith("SELECT ")
]


def verify(client, _writer):
    arguments["verify_cases"](client, cases + prepared_cases)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
