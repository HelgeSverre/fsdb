"""Pin grouped projection-assignment replay boundaries on native MySQL 8.4.11."""

import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)',
     'a\tb\tv\n9\t10\t3\n1\t2\t2\n5\t6\t1\n@n\t@m\n6\t0\n'),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1',
     'a\tb\tv\n9\t10\t3\n@n\t@m\n10\t0\n'),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1',
     'a\tb\tv\n1\t2\t2\n@n\t@m\n2\t0\n'),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0',
     '@n\t@m\n0\t0\n'),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10',
     '@n\t@m\n12\t0\n'),
    ('SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)',
     'a\tb\tv\n109\t110\t3\n101\t102\t2\n105\t106\t1\n@n\t@m\n12\t0\n'),
    ('SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1',
     'a\tb\tv\n109\t110\t3\n@n\t@m\n12\t0\n'),
    ('SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1',
     'a\tb\tv\n101\t102\t2\n@n\t@m\n12\t0\n'),
    ('SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0',
     '@n\t@m\n0\t0\n'),
    ('SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10',
     '@n\t@m\n12\t0\n'),
    ('SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)',
     'a\tb\tv\n109\t110\t3\n101\t102\t2\n105\t106\t1\n@n\t@m\n106\t12\n'),
    ('SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1',
     'a\tb\tv\n109\t110\t3\n@n\t@m\n110\t12\n'),
    ('SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1',
     'a\tb\tv\n101\t102\t2\n@n\t@m\n102\t12\n'),
    ('SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0',
     '@n\t@m\n0\t0\n'),
    ('SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10',
     '@n\t@m\n112\t12\n'),
    ('SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)',
     'a\tb\tv\n3\t3\t1\n1\t1\t2\n5\t5\t3\n@n\t@m\n5\t0\n'),
    ('SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1',
     'a\tb\tv\n3\t3\t1\n@n\t@m\n3\t0\n'),
    ('SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1',
     'a\tb\tv\n1\t1\t2\n@n\t@m\n1\t0\n'),
    ('SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0',
     '@n\t@m\n0\t0\n'),
    ('SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10',
     '@n\t@m\n6\t0\n'),
    ('SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)',
     'a\tb\tv\n3\t3\t1\n1\t1\t2\n5\t5\t3\n@n\t@m\n5\t5\n'),
    ('SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1',
     'a\tb\tv\n3\t3\t1\n@n\t@m\n3\t3\n'),
    ('SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1',
     'a\tb\tv\n1\t1\t2\n@n\t@m\n1\t1\n'),
    ('SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0',
     '@n\t@m\n0\t0\n'),
    ('SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10',
     '@n\t@m\n6\t6\n'),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input ORDER BY IF(a=b,v,-v)',
     'a\tb\tv\n9\t10\t3\n1\t2\t2\n5\t6\t1\n@n\t@m\n12\t0\n'),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY (@m:=v) DESC LIMIT 1',
     'a\tb\tv\n5\t6\t3\n@n\t@m\n6\t3\n'),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING v>1 ORDER BY IF(a=b,v,-v) LIMIT 1',
     'a\tb\tv\n9\t10\t3\n@n\t@m\n10\t0\n'),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input WHERE v>1 GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1',
     'a\tb\tv\n5\t6\t3\n@n\t@m\n6\t0\n'),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING v>1',
     'a\tb\tv\n1\t2\t2\n5\t6\t3\n@n\t@m\n6\t0\n'),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING v>1 ORDER BY v',
     'a\tb\tv\n1\t2\t2\n5\t6\t3\n@n\t@m\n6\t0\n'),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING v>1 ORDER BY IF(a=b,v,-v)',
     'a\tb\tv\n9\t10\t3\n1\t2\t2\n@n\t@m\n2\t0\n'),
    ('SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 2',
     'a\tb\tv\n5\t6\t1\n@n\t@m\n6\t0\n'),
    ('SELECT SQL_CALC_FOUND_ROWS (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0',
     '@n\t@m\n12\t0\n'),
]


def verify(client, _writer):
    arguments["verify_cases"](client, [
        ("CREATE TABLE replay_input(v INT);INSERT INTO replay_input VALUES(2),(1),(3)", ""),
    ])
    for query, expected in cases:
        prepared = "PREPARE projection_replay FROM '" + query.replace("'", "''") + "';EXECUTE projection_replay"
        arguments["verify_cases"](client, [
            ("SET @n=0,@m=0;" + query + ";SELECT @n,@m", expected),
            ("SET @n=0,@m=0;" + prepared + ";SELECT @n,@m", expected),
        ])


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
