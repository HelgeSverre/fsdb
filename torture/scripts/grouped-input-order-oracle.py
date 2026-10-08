"""Verify aggregate-family input ordering and plans on native MySQL 8.4.11."""

import pathlib
import runpy
import subprocess

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))

# The SUM assignment exposes the input order selected for the entire query.
cases = [
    ('SELECT g,SUM(@n:=@n+1) AS s, MIN(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t2\t20\n2\t4\t10\n@n\n3\n', 'Aggregate using temporary table'),
    ('SELECT g,SUM(@n:=@n+1) AS s, MAX(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t2\t20\n2\t4\t30\n@n\n3\n', 'Aggregate using temporary table'),
    ('SELECT g,SUM(@n:=@n+1) AS s, COUNT(DISTINCT 1) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t1\t1\n2\t5\t1\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, SUM(DISTINCT 1) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t1\t1\n2\t5\t1\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, AVG(DISTINCT 1) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t1\t1.0000\n2\t5\t1.0000\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t1\t1\n2\t5\t2\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, SUM(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t1\t20\n2\t5\t40\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, AVG(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t1\t20.0000\n2\t5\t20.0000\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, MIN(v) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t2\t20\n2\t4\t10\n@n\n3\n', 'Aggregate using temporary table'),
    ('SELECT g,SUM(@n:=@n+1) AS s, MAX(v) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t2\t20\n2\t4\t30\n@n\n3\n', 'Aggregate using temporary table'),
    ('SELECT g,SUM(@n:=@n+1) AS s, GROUP_CONCAT(v) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t1\t20\n2\t5\t10,30\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, GROUP_CONCAT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t1\t20\n2\t5\t10,30\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, JSON_ARRAYAGG(v) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t1\t[20]\n2\t5\t[10, 30]\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, JSON_OBJECTAGG(id,v) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t1\t{"2": 20}\n2\t5\t{"1": 10, "3": 30}\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, BIT_AND(v) AS other FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\tother\n1\t2\t20\n2\t4\t10\n@n\n3\n', 'Aggregate using temporary table'),
    ('SELECT g,SUM(@n:=@n+1) AS s FROM group_plan GROUP BY g ORDER BY g',
     'g\ts\n1\t2\n2\t4\n@n\n3\n', 'Aggregate using temporary table'),
    ('SELECT g,SUM(@n:=@n+1) AS s, MIN(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t4\t10\n1\t2\t20\n@n\n3\n', 'Aggregate using temporary table'),
    ('SELECT g,SUM(@n:=@n+1) AS s, MAX(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t4\t30\n1\t2\t20\n@n\n3\n', 'Aggregate using temporary table'),
    ('SELECT g,SUM(@n:=@n+1) AS s, COUNT(DISTINCT 1) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t3\t1\n1\t3\t1\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, SUM(DISTINCT 1) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t3\t1\n1\t3\t1\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, AVG(DISTINCT 1) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t3\t1.0000\n1\t3\t1.0000\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t3\t2\n1\t3\t1\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, SUM(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t3\t40\n1\t3\t20\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, AVG(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t3\t20.0000\n1\t3\t20.0000\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, MIN(v) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t4\t10\n1\t2\t20\n@n\n3\n', 'Aggregate using temporary table'),
    ('SELECT g,SUM(@n:=@n+1) AS s, MAX(v) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t4\t30\n1\t2\t20\n@n\n3\n', 'Aggregate using temporary table'),
    ('SELECT g,SUM(@n:=@n+1) AS s, GROUP_CONCAT(v) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t3\t10,30\n1\t3\t20\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, GROUP_CONCAT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t3\t10,30\n1\t3\t20\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, JSON_ARRAYAGG(v) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t3\t[10, 30]\n1\t3\t[20]\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, JSON_OBJECTAGG(id,v) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t3\t{"1": 10, "3": 30}\n1\t3\t{"2": 20}\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g,SUM(@n:=@n+1) AS s, BIT_AND(v) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'g\ts\tother\n2\t4\t10\n1\t2\t20\n@n\n3\n', 'Aggregate using temporary table'),
    ('SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g DESC',
     'x\ts\tother\n2\t3\t2\n1\t3\t1\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY 1 DESC',
     'x\ts\tother\n2\t3\t2\n1\t3\t1\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY x DESC',
     'x\ts\tother\n2\t3\t2\n1\t3\t1\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g+0 DESC',
     'x\ts\tother\n2\t5\t2\n1\t1\t1\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g DESC,s',
     'x\ts\tother\n2\t5\t2\n1\t1\t1\n@n\n3\n', 'Group aggregate:'),
    ('SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY s,g DESC',
     'x\ts\tother\n1\t1\t1\n2\t5\t2\n@n\n3\n', 'Group aggregate:'),
]


multiple_key_cases = [
    ('SELECT g,h,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g,h ORDER BY g DESC',
     'g\th\ts\tother\n2\t1\t1\t1\n2\t2\t2\t1\n1\t1\t3\t1\n1\t2\t4\t1\n@n\n4\n', "Group aggregate:"),
    ('SELECT g,h,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g,h ORDER BY h DESC',
     'g\th\ts\tother\n1\t2\t2\t1\n2\t2\t4\t1\n1\t1\t1\t1\n2\t1\t3\t1\n@n\n4\n', "Group aggregate:"),
    ('SELECT g,h,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g,h ORDER BY h DESC,g ASC',
     'g\th\ts\tother\n1\t2\t2\t1\n2\t2\t4\t1\n1\t1\t1\t1\n2\t1\t3\t1\n@n\n4\n', "Group aggregate:"),
    ('SELECT g,h,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g,h ORDER BY g DESC,h ASC',
     'g\th\ts\tother\n2\t1\t1\t1\n2\t2\t2\t1\n1\t1\t3\t1\n1\t2\t4\t1\n@n\n4\n', "Group aggregate:"),
    ('SELECT g,h,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g,h ORDER BY g DESC,h DESC,s',
     'g\th\ts\tother\n2\t2\t4\t1\n2\t1\t3\t1\n1\t2\t2\t1\n1\t1\t1\t1\n@n\n4\n', "Group aggregate:"),
]

def verify_queries(client, queries):
    for query, expected, operator in queries:
        prepared = "PREPARE group_order FROM '" + query.replace("'", "''") + "';EXECUTE group_order"
        arguments["verify_cases"](client, [
            ("SET @n=0;" + query + ";SELECT @n", expected),
            ("SET @n=0;" + prepared + ";SELECT @n", expected),
        ])
        plan = subprocess.run(
            [*client.process.args, "--column-names", "-e",
             "USE probe;EXPLAIN FORMAT=TREE " + query],
            capture_output=True, text=True, check=True,
        ).stdout
        if operator not in plan:
            raise AssertionError(f"{query}\nExpected {operator!r} in plan:\n{plan}")
        if operator == "Group aggregate:" and "Sort: group_plan.g" not in plan:
            raise AssertionError(f"Expected sorted grouping input:\n{plan}")
        print(query + "\n" + plan, flush=True)


def verify(client, _writer):
    arguments["verify_cases"](client, [
        ("CREATE TABLE group_plan(id INT PRIMARY KEY,g INT,v INT);"
         "INSERT INTO group_plan VALUES(1,2,10),(2,1,20),(3,2,30)", ""),
    ])
    verify_queries(client, cases)
    arguments["verify_cases"](client, [
        ("DROP TABLE group_plan;CREATE TABLE group_plan(id INT PRIMARY KEY,g INT,h INT,v INT);"
         "INSERT INTO group_plan VALUES(1,2,2,10),(2,1,1,20),(3,2,1,30),(4,1,2,40)", ""),
    ])
    verify_queries(client, multiple_key_cases)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
