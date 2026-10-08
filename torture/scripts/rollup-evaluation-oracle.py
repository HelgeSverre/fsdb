"""Pin volatile ROLLUP levels and LIMIT evaluation on native MySQL 8.4.11."""

import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ('SELECT g,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g WITH ROLLUP',
     'g\ts\n1\t6\n2\t24\nNULL\t25\n@n\n10\n'),
    ('SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP',
     'g\th\ts\n1\t1\t3\n1\t2\t6\n1\tNULL\t7\n2\t1\t21\n2\t2\t15\n2\tNULL\t33\nNULL\tNULL\t35\n@n\n15\n'),
    ('SELECT g,h,SUM(@n:=@n+1) AS a,SUM(@n:=@n+1) AS b FROM rollup_input GROUP BY g,h WITH ROLLUP',
     'g\th\ta\tb\n1\t1\t3\t6\n1\t2\t9\t12\n1\tNULL\t10\t16\n2\t1\t36\t42\n2\t2\t27\t30\n2\tNULL\t60\t69\nNULL\tNULL\t65\t80\n@n\n30\n'),
    ('SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP ORDER BY g DESC,h DESC',
     'g\th\ts\n2\t2\t3\n2\t1\t15\n2\tNULL\t15\n1\t2\t12\n1\t1\t15\n1\tNULL\t25\nNULL\tNULL\t35\n@n\n15\n'),
    ('SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP HAVING GROUPING(g,h)=0',
     'g\th\ts\n1\t1\t3\n1\t2\t6\n2\t1\t21\n2\t2\t15\n@n\n15\n'),
    ('SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP LIMIT 1',
     'g\th\ts\n1\t1\t3\n@n\n3\n'),
    ('SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP LIMIT 0',
     '@n\n0\n'),
    ('SELECT g,h,COUNT(DISTINCT (@n:=@n+1)) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP',
     'g\th\ts\n1\t1\t1\n1\t2\t1\n1\tNULL\t2\n2\t1\t2\n2\t2\t1\n2\tNULL\t3\nNULL\tNULL\t5\n@n\n5\n'),
    ('SELECT g,h,GROUP_CONCAT(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP',
     'g\th\ts\n1\t1\t1\n1\t2\t2\n1\tNULL\t1,2\n2\t1\t3,4\n2\t2\t5\n2\tNULL\t3,4,5\nNULL\tNULL\t1,2,3,4,5\n@n\n5\n'),
    ('SELECT g,h,JSON_ARRAYAGG(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP',
     'g\th\ts\n1\t1\t[3]\n1\t2\t[6]\n1\tNULL\t[2, 5]\n2\t1\t[9, 12]\n2\t2\t[15]\n2\tNULL\t[8, 11, 14]\nNULL\tNULL\t[1, 4, 7, 10, 13]\n@n\n15\n'),
    ('SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input WHERE FALSE GROUP BY g,h WITH ROLLUP',
     '@n\n0\n'),
    ('SELECT g,h,SUM(DISTINCT (@n:=@n+1)) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP',
     'g\th\ts\n1\t1\t3\n1\t2\t6\n1\tNULL\t7\n2\t1\t21\n2\t2\t15\n2\tNULL\t33\nNULL\tNULL\t35\n@n\n15\n'),
    ('SELECT g,h,MIN(@n:=@n+1) AS lo,MAX(@n:=@n+1) AS hi FROM rollup_input GROUP BY g,h WITH ROLLUP',
     'g\th\tlo\thi\n1\t1\t3\t6\n1\t2\t9\t12\n1\tNULL\t2\t11\n2\t1\t15\t24\n2\t2\t27\t30\n2\tNULL\t14\t29\nNULL\tNULL\t1\t28\n@n\n30\n'),
    ('SELECT g,h,SUM(@n:=@n+1) AS s,COUNT(DISTINCT (@n:=@n+1)) AS n FROM rollup_input GROUP BY g,h WITH ROLLUP',
     'g\th\ts\tn\n1\t1\t3\t1\n1\t2\t7\t1\n1\tNULL\t8\t2\n2\t1\t26\t2\n2\t2\t19\t1\n2\tNULL\t42\t3\nNULL\tNULL\t45\t5\n@n\n20\n'),
    ('SELECT g,h,SUM(@n:=@n+1) AS s,GROUP_CONCAT(@n:=@n+1) AS c FROM rollup_input GROUP BY g,h WITH ROLLUP',
     'g\th\ts\tc\n1\t1\t3\t4\n1\t2\t7\t8\n1\tNULL\t8\t4,8\n2\t1\t26\t12,16\n2\t2\t19\t20\n2\tNULL\t42\t12,16,20\nNULL\tNULL\t45\t4,8,12,16,20\n@n\n20\n'),
    ('SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP ORDER BY g DESC,h DESC LIMIT 1',
     'g\th\ts\n2\t2\t3\n@n\n15\n'),
    ('SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP ORDER BY s DESC LIMIT 1',
     'g\th\ts\nNULL\tNULL\t35\n@n\n15\n'),
    ('SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP LIMIT 1 OFFSET 2',
     'g\th\ts\n1\tNULL\t7\n@n\n6\n'),
    ('SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP HAVING GROUPING(g,h)=3 LIMIT 1',
     'g\th\ts\nNULL\tNULL\t35\n@n\n15\n'),
]


def verify(client, _writer):
    arguments["verify_cases"](client, [
        ("CREATE TABLE rollup_input(id INT PRIMARY KEY,g INT,h INT);"
         "INSERT INTO rollup_input VALUES(1,2,2),(2,1,1),(3,2,1),(4,1,2),(5,2,1)", ""),
    ])
    for query, expected in cases:
        prepared = "PREPARE rollup_evaluation FROM '" + query.replace("'", "''") + "';EXECUTE rollup_evaluation"
        arguments["verify_cases"](client, [
            ("SET @n=0;" + query + ";SELECT @n", expected),
            ("SET @n=0;" + prepared + ";SELECT @n", expected),
        ])


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
