"""Verify relation-name diagnostics on disposable native MySQL 8.4.11."""

import pathlib
import re
import runpy
import subprocess

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))
expect = oracle["expect"]


def verify(client, _writer):
    client.query('CREATE DATABASE other;USE probe;CREATE TABLE a(id INT);CREATE TABLE b(id INT);CREATE TABLE c(id INT);CREATE TABLE other.a(id INT);CREATE TABLE other.x(id INT);INSERT INTO a VALUES(1);INSERT INTO b VALUES(1)')
    for query, expected in [
        ('SELECT * FROM a x JOIN other.a x ON 1', None),
        ('SELECT * FROM a a JOIN (SELECT 1 AS id) a ON 1', None),
        ('WITH a AS (SELECT 1 AS id) SELECT * FROM a JOIN probe.a ON 1', None),
        ('WITH d AS (SELECT 1 AS id) SELECT * FROM d x JOIN d x ON 1', (1066, '42000', "Not unique table/alias: 'x'")),
        ('SELECT * FROM a JOIN a ON 1', (1066, '42000', "Not unique table/alias: 'a'")),
        ('SELECT a.id FROM a JOIN (a JOIN c ON a.id=c.id) ON 1', (1066, '42000', "Not unique table/alias: 'a'")),
        ('SELECT * FROM a x JOIN (b x JOIN c y ON 1) ON 1', (1066, '42000', "Not unique table/alias: 'x'")),
        ('SELECT * FROM a x JOIN b X ON 1', (1066, '42000', "Not unique table/alias: 'X'")),
        ('SELECT * FROM probe.a JOIN other.a ON 1', None),
        ('SELECT * FROM a x JOIN other.x ON 1', None),
        ('SELECT * FROM a JOIN (SELECT 1 AS id) a ON 1', None),
        ('SELECT * FROM (SELECT 1 AS id) d JOIN (SELECT 2 AS id) d ON 1', (1066, '42000', "Not unique table/alias: 'd'")),
        ('SELECT a.id,(SELECT a.id FROM a WHERE a.id=1) FROM a', None),
        ('SELECT * FROM absent x JOIN absent x ON 1', (1066, '42000', "Not unique table/alias: 'x'")),
        ('SELECT * FROM (SELECT 1 AS id,2 AS id) d', (1060, '42S21', "Duplicate column name 'id'")),
        ('SELECT * FROM (SELECT a.id,b.id FROM a JOIN b ON 1) d', (1060, '42S21', "Duplicate column name 'id'")),
        ('SELECT * FROM (SELECT 1,1) d', (1060, '42S21', "Duplicate column name '1'")),
        ('SELECT * FROM (SELECT a.id,a.id+0 AS id FROM a) d', (1060, '42S21', "Duplicate column name 'id'")),
        ('SELECT * FROM (SELECT 1 AS id,2 AS ID) d', (1060, '42S21', "Duplicate column name 'ID'")),
        ('SELECT * FROM a LEFT JOIN (SELECT 1 AS id,2 AS id) d ON 0', (1060, '42S21', "Duplicate column name 'id'")),
        ('WITH d AS (SELECT a.id,b.id FROM a JOIN b ON 1) SELECT * FROM d', (1060, '42S21', "Duplicate column name 'id'")),
        ('WITH d(x,y) AS (SELECT a.id,b.id FROM a JOIN b ON 1) SELECT * FROM d', None),
        ('WITH d(x,x) AS (SELECT 1,2) SELECT * FROM d', (1060, '42S21', "Duplicate column name 'x'")),
        ('WITH d(x,x) AS (SELECT 1,2) SELECT 1', None),
        ('WITH RECURSIVE d AS (SELECT 1 AS n,2 AS n UNION ALL SELECT n+1,n+2 FROM d WHERE n<2) SELECT * FROM d', (1060, '42S21', "Duplicate column name 'n'")),
        ('WITH RECURSIVE d(x,y) AS (SELECT 1 AS n,2 AS n UNION ALL SELECT x+1,y+1 FROM d WHERE x<2) SELECT * FROM d', None),
        ('WITH d AS (SELECT 1),d AS (SELECT 2) SELECT 1', (1066, '42000', "Not unique table/alias: 'd'")),
        ('UPDATE a x JOIN b x ON 1 SET x.id=2', (1066, '42000', "Not unique table/alias: 'x'")),
        ('DELETE x FROM a x JOIN b x ON 1', (1066, '42000', "Not unique table/alias: 'x'")),
    ]:
        for statement in [query, "PREPARE relation_names FROM '" + query + "'"]:
            result = subprocess.run([*client.process.args, "-e", "USE probe;" + statement], capture_output=True, text=True, check=False)
            error = re.search(r"ERROR (\d+) \((\w+)\).*?: (.*)", result.stderr)
            actual = (int(error[1]), error[2], error[3]) if error else None
            if expected is None:
                expect("valid relation namespace", result.returncode, 0)
            expect("relation-name diagnostic", actual, expected)
    expect("renamed CTE", client.query("USE probe;WITH d(x,y) AS (SELECT a.id,b.id FROM a JOIN b ON 1) SELECT * FROM d"), "1\t1")
    expect("renamed recursive CTE", client.query("USE probe;WITH RECURSIVE d(x,y) AS (SELECT 1 AS n,2 AS n UNION ALL SELECT x+1,y+1 FROM d WHERE x<2) SELECT * FROM d"), "1\t2\n2\t3")
    rejected = subprocess.run([*client.process.args, "--force"], input="USE probe;SET @touches=0;\nSELECT * FROM (SELECT @touches:=@touches+1 AS id,1 AS id) d;\nSELECT @touches;\n", capture_output=True, text=True, check=False)
    expect("duplicate columns rejected before evaluation", rejected.stdout.strip(), "0")
    expect("duplicate expression error", "ERROR 1060 (42S21)" in rejected.stderr, True)


if __name__ == "__main__":
    oracle["run"](verify)
