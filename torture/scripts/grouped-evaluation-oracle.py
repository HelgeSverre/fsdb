"""Pin grouped expression evaluation and returned-row side effects on MySQL 8.4.11."""

import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
inputs = ["(2),(1)", "(2),(1),(3)", "(2),(1),(2)"]

# Each output corresponds to the input fixture at the same position.
cases = [
    ('SELECT SUM(@n:=@n+1) AS s FROM eval_order',
     ['s\n3\n@n\n2\n', 's\n6\n@n\n3\n', 's\n6\n@n\n3\n']),
    ('SELECT SUM(@n:=@n+1) AS s FROM eval_order ORDER BY s',
     ['s\n3\n@n\n2\n', 's\n6\n@n\n3\n', 's\n6\n@n\n3\n']),
    ('SELECT SUM(@n:=@n+1) AS s FROM eval_order ORDER BY ABS(s)',
     ['s\n3\n@n\n2\n', 's\n6\n@n\n3\n', 's\n6\n@n\n3\n']),
    ('SELECT SUM(@n:=@n+1) AS s FROM eval_order ORDER BY SUM(@n:=@n+1)',
     ['s\n3\n@n\n2\n', 's\n6\n@n\n3\n', 's\n6\n@n\n3\n']),
    ('SELECT SUM(@n:=@n+1) AS s FROM eval_order ORDER BY ABS(SUM(@n:=@n+1))',
     ['s\n3\n@n\n2\n', 's\n6\n@n\n3\n', 's\n6\n@n\n3\n']),
    ('SELECT SUM(@n:=@n+1) AS s FROM eval_order GROUP BY v',
     ['s\n1\n2\n@n\n2\n', 's\n1\n2\n3\n@n\n3\n', 's\n4\n2\n@n\n3\n']),
    ('SELECT SUM(@n:=@n+1) AS s FROM eval_order GROUP BY v ORDER BY s',
     ['s\n1\n2\n@n\n2\n', 's\n1\n2\n3\n@n\n3\n', 's\n2\n4\n@n\n3\n']),
    ('SELECT SUM(@n:=@n+1) AS s FROM eval_order GROUP BY v ORDER BY ABS(s)',
     ['s\n1\n2\n@n\n2\n', 's\n1\n2\n3\n@n\n3\n', 's\n2\n4\n@n\n3\n']),
    ('SELECT SUM(@n:=@n+1) AS s FROM eval_order GROUP BY v ORDER BY SUM(@n:=@n+1)',
     ['s\n1\n2\n@n\n2\n', 's\n1\n2\n3\n@n\n3\n', 's\n2\n4\n@n\n3\n']),
    ('SELECT SUM(@n:=@n+1) AS s FROM eval_order GROUP BY v ORDER BY ABS(SUM(@n:=@n+1))',
     ['s\n2\n4\n@n\n4\n', 's\n2\n4\n6\n@n\n6\n', 's\n4\n8\n@n\n6\n']),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM eval_order GROUP BY v ORDER BY a',
     ['a\tb\tv\n1\t2\t2\n3\t4\t1\n@n\n4\n', 'a\tb\tv\n1\t2\t2\n3\t4\t1\n5\t6\t3\n@n\n6\n', 'a\tb\tv\n1\t2\t2\n3\t4\t1\n@n\n4\n']),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM eval_order GROUP BY v ORDER BY a+0',
     ['a\tb\tv\n1\t2\t2\n4\t5\t1\n@n\n5\n', 'a\tb\tv\n1\t2\t2\n4\t5\t1\n7\t8\t3\n@n\n8\n', 'a\tb\tv\n1\t2\t2\n4\t5\t1\n@n\n5\n']),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM eval_order GROUP BY v ORDER BY a+b',
     ['a\tb\tv\n1\t2\t2\n5\t6\t1\n@n\n6\n', 'a\tb\tv\n1\t2\t2\n5\t6\t1\n9\t10\t3\n@n\n10\n', 'a\tb\tv\n1\t2\t2\n5\t6\t1\n@n\n6\n']),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM eval_order GROUP BY v ORDER BY IF(a=b,v,-v)',
     ['a\tb\tv\n1\t2\t2\n5\t6\t1\n@n\n6\n', 'a\tb\tv\n9\t10\t3\n1\t2\t2\n5\t6\t1\n@n\n6\n', 'a\tb\tv\n1\t2\t2\n5\t6\t1\n@n\n6\n']),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM eval_order GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1',
     ['a\tb\tv\n1\t2\t2\n@n\n2\n', 'a\tb\tv\n9\t10\t3\n@n\n10\n', 'a\tb\tv\n1\t2\t2\n@n\n2\n']),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM eval_order GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0',
     ['@n\n0\n', '@n\n0\n', '@n\n0\n']),
    ('SELECT SUM(@n:=@n+1) AS a,SUM(@n:=@n+1) AS b FROM eval_order GROUP BY v ORDER BY ABS(SUM(@n:=@n+1))',
     ['a\tb\n2\t3\n5\t6\n@n\n6\n', 'a\tb\n2\t3\n5\t6\n8\t9\n@n\n9\n', 'a\tb\n5\t6\n10\t12\n@n\n9\n']),
    ('SELECT SUM(@n:=@n+1) AS s FROM eval_order GROUP BY v HAVING TRUE ORDER BY ABS(SUM(@n:=@n+1))',
     ['s\n2\n4\n@n\n4\n', 's\n2\n4\n6\n@n\n6\n', 's\n4\n8\n@n\n6\n']),
]


def verify(client, _writer):
    for query, outputs in cases:
        for values, expected in zip(inputs, outputs, strict=True):
            setup = ("DROP TABLE IF EXISTS eval_order;CREATE TABLE eval_order(v INT);"
                     "INSERT INTO eval_order VALUES" + values + ";SET @n=0;")
            prepared = "PREPARE grouped_eval FROM '" + query.replace("'", "''") + "';EXECUTE grouped_eval;"
            arguments["verify_cases"](client, [
                (setup + query + ";SELECT @n", expected),
                (setup + prepared + "SELECT @n", expected),
            ])


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
