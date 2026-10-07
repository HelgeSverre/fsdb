"""Verify grouped right join operands on disposable native MySQL 8.4.11."""

import pathlib
import re
import runpy
import subprocess

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))
expect = oracle["expect"]


def verify(client, _writer):
    client.query("CREATE TABLE probe.a(id INT);CREATE TABLE probe.b(id INT);CREATE TABLE probe.c(id INT);"
                 "INSERT INTO probe.a VALUES(1),(2);INSERT INTO probe.b VALUES(1),(3);INSERT INTO probe.c VALUES(3)")
    for query, expected in [
        ('SELECT a.id FROM a JOIN (a JOIN c ON a.id=c.id) ON 1', (1066, '42000')),
        ('WITH d AS (SELECT * FROM a JOIN b ON a.id=b.id) SELECT 1 UNION ALL SELECT id FROM d', (1060, '42S21')),
        ('WITH bad AS (SELECT * FROM a JOIN b USING(missing)) SELECT (WITH unused AS (SELECT * FROM bad) SELECT 1)', None),
        ('WITH d AS (SELECT * FROM a JOIN b USING(missing)) SELECT (WITH d AS (SELECT 1 AS id) SELECT id FROM d)', None),
        ('WITH d AS (SELECT * FROM a JOIN b USING(missing)) SELECT 1', None),
        ('SELECT a.id AS chosen,(SELECT COUNT(*) FROM b JOIN c ON c.id=chosen) FROM a', None),
        ('WITH bad AS (SELECT * FROM a JOIN b USING(missing)),good AS (SELECT * FROM c) SELECT * FROM good', None),
        ('SELECT a.id FROM a JOIN (b JOIN c ON EXISTS(SELECT a.id)) ON 1', (1054, '42S22')),
        ('WITH d AS (SELECT a.id FROM a JOIN b ON a.id=b.id) SELECT 1 UNION ALL SELECT id FROM d', None),
        ('WITH d AS (SELECT a.id FROM a JOIN b ON b.missing=a.id) SELECT 1 UNION ALL SELECT id FROM d', (1054, '42S22')),
        ('SELECT a.id FROM a JOIN b ON b.missing=a.id', (1054, '42S22')),
        ('SELECT a.id FROM a JOIN b ON missing=a.id', (1054, '42S22')),
        ('SELECT a.id FROM a JOIN b ON id=a.id', (1052, '23000')),
        ('SELECT a.id FROM a JOIN b ON c.id=a.id JOIN c ON 1', (1054, '42S22')),
        ('SELECT a.id FROM a LEFT JOIN (b JOIN c ON c.id=a.id) ON a.id=b.id', (1054, '42S22')),
        ('SELECT a.id FROM a JOIN (b RIGHT JOIN c USING(id)) ON id=a.id', (1052, '23000')),
        ('SELECT c.id FROM (SELECT 3 AS wanted) d JOIN (a RIGHT JOIN c USING(id)) ON id=d.wanted', None),
        ('SELECT b.id FROM a RIGHT JOIN b USING(id) JOIN (SELECT 3 AS wanted) d ON id=d.wanted', None),
        ('SELECT a.id,(SELECT COUNT(*) FROM b JOIN (c JOIN (SELECT 1 AS seed) d ON c.id=a.id+1) ON 1) FROM a', None),
        ('SELECT a.id,(SELECT COUNT(*) FROM (SELECT 1 AS other) b JOIN (SELECT 1 AS seed) c ON id=1) FROM a', None),
        ('SELECT a.id,(SELECT COUNT(*) FROM (SELECT 1 AS other) a JOIN b ON a.id=b.id) FROM a', None),
        ('SELECT a.id FROM a JOIN (b JOIN c ON EXISTS(SELECT 1 WHERE a.id=c.id)) ON 1', (1054, '42S22')),
        ('SELECT a.id FROM a JOIN b ON EXISTS(SELECT 1 WHERE c.id=a.id) JOIN c ON 1', (1054, '42S22')),
        ('SELECT a.id AS chosen FROM a JOIN b ON chosen=b.id', (1054, '42S22')),
        ('SELECT a.id,(SELECT COUNT(*) FROM (SELECT b.id FROM b JOIN c ON c.id=a.id) d) FROM a', None),
        ('SELECT a.id FROM a JOIN LATERAL (SELECT b.id FROM b JOIN c ON c.id=a.id) d ON 1', None),
        ('SELECT a.id FROM a JOIN (b JOIN LATERAL (SELECT c.id FROM c JOIN (SELECT 1 AS seed) d ON c.id=a.id) x ON 1) ON 1', None),
        ('SELECT a.id FROM a JOIN (SELECT b.id FROM b JOIN c ON c.id=a.id) d ON 1', (1054, '42S22')),
        ('WITH d AS (SELECT a.id FROM a JOIN b ON b.missing=a.id) SELECT * FROM d', (1054, '42S22')),
        ('WITH d AS (SELECT a.id FROM a JOIN b ON b.missing=a.id) SELECT 1', None),
        ('UPDATE a JOIN (b JOIN c ON c.id=a.id) ON 1 SET a.id=7', (1054, '42S22')),
        ('DELETE a FROM a JOIN (b JOIN c ON c.id=a.id) ON 1', (1054, '42S22')),
        ('SELECT a.id FROM a JOIN (b JOIN c ON b.missing=c.id) ON 0', (1054, '42S22')),
    ]:
        prepared = subprocess.run([*client.process.args, "-e", "USE probe;PREPARE scoped_join FROM '" + query + "'"], capture_output=True, text=True, check=False)
        error = re.search(r"ERROR (\d+) \((\w+)\)", prepared.stderr)
        actual = (int(error[1]), error[2]) if error else None
        if expected is None:
            expect("valid prepared join scope", prepared.returncode, 0)
        expect("prepared join scope", actual, expected)
    for source, expected in [
        ("a,(b JOIN c ON b.id=c.id)", "1\t3\t3\n2\t3\t3"),
        ("a LEFT JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id", "1\tNULL\tNULL\n2\tNULL\tNULL"),
        ("(a LEFT JOIN b ON a.id=b.id) JOIN c ON b.id=c.id", ""),
        ("a JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id", ""),
        ("a LEFT JOIN (b,c) ON a.id=b.id", "1\t1\t3\n2\tNULL\tNULL"),
        ("a LEFT JOIN (b LEFT JOIN c ON b.id=c.id) ON a.id=b.id", "1\t1\tNULL\n2\tNULL\tNULL"),
    ]:
        query = "SELECT a.id,b.id,c.id FROM " + source + " ORDER BY a.id,b.id,c.id"
        expect(source, client.query("USE probe;" + query), expected)
    for query, expected in [
        ("SELECT * FROM a LEFT JOIN (b JOIN c USING(id)) ON a.id=b.id ORDER BY a.id", "1\tNULL\n2\tNULL"),
        ("SELECT * FROM a LEFT JOIN (b LEFT JOIN c USING(id)) ON a.id=b.id ORDER BY a.id", "1\t1\n2\tNULL"),
        ("SELECT * FROM a LEFT JOIN (b JOIN c USING(id)) USING(id) ORDER BY id", "1\n2"),
    ]:
        expect("grouped USING projection", client.query("USE probe;" + query), expected)
    correlated = "SELECT a.id,(SELECT COUNT(*) FROM b JOIN (c JOIN (SELECT 1 AS seed) d ON c.id=a.id+1) ON 1) AS n FROM a ORDER BY a.id"
    expect("grouped correlated rows", client.query("USE probe;" + correlated), "1\t0\n2\t2")
    plan = client.query("EXPLAIN " + correlated)
    dependent = [row.split("\t")[1] for row in plan.splitlines() if row.split("\t")[0] == "2"]
    expect("grouped correlated plan", set(dependent), {"DEPENDENT SUBQUERY"})
    for query, code in [
        ("SELECT * FROM a LEFT JOIN (b JOIN (SELECT 1 AS other) c USING(id)) ON a.id=b.id", 1054),
        ("SELECT * FROM a JOIN (b JOIN c ON b.id=c.id) USING(id)", 1052),
        ("SELECT a.id FROM a JOIN (b RIGHT JOIN c USING(id)) ON id=a.id", 1052),
        ("SELECT * FROM (SELECT * FROM a JOIN (b JOIN c ON b.id=c.id) USING(id)) d", 1052),
        ("WITH d AS (SELECT * FROM a JOIN (b JOIN c ON b.id=c.id) USING(id)) SELECT * FROM d", 1052),
        ("SELECT (SELECT COUNT(*) FROM a JOIN (b JOIN c ON b.id=c.id) USING(id))", 1052),
        ("SELECT 1 WHERE EXISTS(SELECT * FROM a JOIN (b JOIN c ON b.id=c.id) USING(id))", 1052),
        ("SELECT id FROM a UNION ALL SELECT id FROM b JOIN c USING(missing)", 1054),
        ("UPDATE a JOIN (b JOIN c ON b.id=c.id) USING(id) SET a.id=7", 1052),
        ("DELETE a FROM a JOIN (b JOIN c ON b.id=c.id) USING(id)", 1052),
        ("INSERT INTO a SELECT a.id FROM a JOIN (b JOIN c ON b.id=c.id) USING(id)", 1052),
    ]:
        for statement in [query, "PREPARE invalid_group FROM '" + query + "'"]:
            rejected = subprocess.run([*client.process.args, "-e", "USE probe;" + statement], capture_output=True, text=True, check=False)
            error = re.search(r"ERROR (\d+) \((\w+)\)", rejected.stderr)
            expect("invalid grouped USING", (int(error[1]), error[2]) if error else None, (code, "42S22" if code == 1054 else "23000"))
    client.query("USE probe;CREATE VIEW grouped_using_view(aid,shared_id) AS SELECT * FROM a LEFT JOIN (b LEFT JOIN c USING(id)) ON a.id=b.id")
    expect("grouped USING stored view", client.query("USE probe;SELECT * FROM grouped_using_view ORDER BY aid"), "1\t1\n2\tNULL")
    client.query("USE probe;INSERT INTO c VALUES(2)")
    expect("enclosing USING reads right-preserved value", client.query("USE probe;SELECT a.id,b.id,c.id FROM a LEFT JOIN (b RIGHT JOIN c USING(id)) USING(id) ORDER BY a.id"), "1\tNULL\tNULL\n2\tNULL\t2")
    client.query("USE probe;DELETE FROM c WHERE id=2")
    client.query("USE probe;CREATE TABLE grouped_upsert(id INT PRIMARY KEY,n INT);INSERT INTO grouped_upsert VALUES(1,0)")
    client.query("USE probe;INSERT INTO grouped_upsert SELECT 1,0 ON DUPLICATE KEY UPDATE n=(SELECT MAX(b.id) FROM a JOIN (b JOIN c ON b.id=c.id) ON 1)")
    expect("grouped upsert subquery scope", client.query("USE probe;SELECT n FROM grouped_upsert"), "3")
    client.query("USE probe;CREATE TABLE grouped_docs(id INT PRIMARY KEY,body TEXT,FULLTEXT(body));INSERT INTO grouped_docs VALUES(1,'orchard apples'),(2,'coastal pears')")
    expect("grouped full-text source scope", client.query("USE probe;SELECT d.id FROM grouped_docs d LEFT JOIN (b JOIN c ON b.id=c.id) ON d.id=b.id WHERE MATCH(d.body) AGAINST('orchard')"), "1")
    expect("grouped full-text LIMIT preserves fallback execution", client.query("USE probe;SELECT d.id FROM grouped_docs d JOIN (b JOIN c ON b.id=c.id) ON 1 WHERE MATCH(d.body) AGAINST('orchard') LIMIT 1"), "1")
    expect("full-text source inside grouped operand", client.query("USE probe;SELECT d.id FROM b JOIN (grouped_docs d JOIN c ON 1) ON 1 WHERE MATCH(d.body) AGAINST('orchard') LIMIT 1"), "1")
    client.query("USE probe;CREATE TABLE grouped_on_docs(id INT PRIMARY KEY,body TEXT,n INT,FULLTEXT(body));INSERT INTO grouped_on_docs VALUES(1,'orchard apples',0),(2,'coastal pears',0)")
    source = "a JOIN (grouped_on_docs d JOIN c ON MATCH(d.body) AGAINST('orchard')) ON a.id=d.id"
    expect("MATCH inside grouped ON", client.query("USE probe;SELECT d.id FROM " + source), "1")
    client.query("USE probe;UPDATE " + source + " SET d.n=9")
    expect("UPDATE MATCH inside grouped ON", client.query("USE probe;SELECT id,n FROM grouped_on_docs ORDER BY id"), "1\t9\n2\t0")
    client.query("USE probe;DELETE d FROM " + source)
    expect("DELETE MATCH inside grouped ON", client.query("USE probe;SELECT id FROM grouped_on_docs"), "2")
    type_args = [arg for arg in client.process.args if arg not in ["--batch", "--raw", "--skip-column-names"]]
    for query, expected_types, expected_owners in [
        ("SELECT * FROM a LEFT JOIN (b LEFT JOIN c USING(id)) ON a.id=b.id", ["LONG", "LONG"], ["a", "b"]),
        ("SELECT * FROM a LEFT JOIN (b RIGHT JOIN c USING(id)) ON a.id=b.id", ["LONG", "LONG"], ["a", "c"]),
        ("SELECT * FROM a LEFT JOIN (b JOIN c USING(id)) ON a.id=b.id", ["LONG", "LONG"], ["a", "b"]),
        ("SELECT COALESCE(b.id,c.id) FROM b LEFT JOIN c USING(id)", ["LONG"], [""]),
        ("SELECT * FROM (SELECT * FROM b RIGHT JOIN c USING(id)) merged", ["LONG"], ["c"]),
        ("WITH merged AS (SELECT * FROM b RIGHT JOIN c USING(id)) SELECT * FROM merged", ["LONG"], ["c"]),
        ("SELECT x FROM (SELECT id AS x FROM b RIGHT JOIN c USING(id)) merged", ["LONG"], ["c"]),
        ("WITH merged(x) AS (SELECT * FROM b RIGHT JOIN c USING(id)) SELECT x FROM merged", ["LONG"], ["c"]),
    ]:
        described = subprocess.run([*type_args, "--column-type-info", "-vvv", "-e", "USE probe;" + query], capture_output=True, text=True, check=True)
        expect("grouped integer field types", re.findall(r"^Type:\s+(\w+)", described.stdout, re.MULTILINE), expected_types)
        expect("grouped field owners", re.findall(r"^Org_table:\s+`([^`]*)`", described.stdout, re.MULTILINE), expected_owners)
    for source in ["b JOIN chosen c ON b.id=c.id", "b JOIN c ON b.id=c.id AND EXISTS (SELECT 1 FROM chosen WHERE chosen.id=c.id)"]:
        query = ("WITH chosen AS (SELECT 3 AS id) SELECT a.id,b.id,c.id FROM a LEFT JOIN ("
                 + source
                 + ") ON a.id=b.id ORDER BY a.id,b.id,c.id")
        expect("grouped CTE dependencies", client.query("USE probe;" + query), "1\tNULL\tNULL\n2\tNULL\tNULL")
    expect("CTE over grouped sources retains origins", client.query("USE probe;WITH grouped AS (SELECT a.id AS aid,b.id AS bid FROM a LEFT JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id) SELECT aid,bid FROM grouped ORDER BY aid"), "1\tNULL\n2\tNULL")
    client.query("USE probe;CREATE TABLE grouped_keys(id INT PRIMARY KEY,n INT);INSERT INTO grouped_keys VALUES(1,0),(2,0),(3,0);INSERT INTO c VALUES(2)")
    for source in ["JOIN (b RIGHT JOIN c USING(id)) USING(id)", "NATURAL JOIN (b NATURAL RIGHT JOIN c)"]:
        client.query("USE probe;UPDATE grouped_keys " + source + " SET n=9")
        expect("grouped mutation reads merged key", client.query("USE probe;SELECT id,n FROM grouped_keys ORDER BY id"), "1\t0\n2\t9\n3\t9")
        client.query("USE probe;UPDATE grouped_keys SET n=0")
    client.query("USE probe;DELETE grouped_keys FROM grouped_keys JOIN (b RIGHT JOIN c USING(id)) USING(id)")
    expect("grouped DELETE reads merged key", client.query("USE probe;SELECT id FROM grouped_keys"), "1")
    client.query("USE probe;DELETE FROM c WHERE id=2")
    expect("ON reads left merged key", client.query("USE probe;SELECT b.id FROM a RIGHT JOIN b USING(id) JOIN (SELECT 3 AS wanted) d ON id=d.wanted"), "3")
    expect("ON reads grouped right merged key", client.query("USE probe;SELECT c.id FROM (SELECT 3 AS wanted) d JOIN (a RIGHT JOIN c USING(id)) ON id=d.wanted"), "3")
    client.query("USE probe;CREATE TABLE on_b(id INT);INSERT INTO on_b VALUES(1),(3)")
    client.query("USE probe;UPDATE a RIGHT JOIN on_b USING(id) JOIN (SELECT 3 AS wanted) d ON id=d.wanted SET on_b.id=4")
    expect("UPDATE ON reads merged key", client.query("USE probe;SELECT id FROM on_b ORDER BY id"), "1\n4")
    client.query("USE probe;DELETE on_b FROM a RIGHT JOIN on_b USING(id) JOIN (SELECT 4 AS wanted) d ON id=d.wanted")
    expect("DELETE ON reads merged key", client.query("USE probe;SELECT id FROM on_b"), "1")
    expect("chained USING reads left merged key", client.query("USE probe;SELECT b.id FROM a RIGHT JOIN b USING(id) JOIN c USING(id)"), "3")
    expect("grouped chained USING reads left merged key", client.query("USE probe;SELECT b.id FROM (SELECT 1 AS seed) d JOIN (a RIGHT JOIN b USING(id) JOIN c USING(id)) ON 1"), "3")
    client.query("USE probe;CREATE TABLE chained_a(id INT);CREATE TABLE chained_b(id INT,n INT);CREATE TABLE chained_c(id INT);INSERT INTO chained_a VALUES(1),(2);INSERT INTO chained_b VALUES(1,0),(3,0);INSERT INTO chained_c VALUES(3)")
    client.query("USE probe;UPDATE chained_a RIGHT JOIN chained_b USING(id) JOIN chained_c USING(id) SET chained_b.n=9")
    expect("chained UPDATE reads left merged key", client.query("USE probe;SELECT id,n FROM chained_b ORDER BY id"), "1\t0\n3\t9")
    client.query("USE probe;DELETE chained_b FROM chained_a RIGHT JOIN chained_b USING(id) JOIN chained_c USING(id)")
    expect("chained DELETE reads left merged key", client.query("USE probe;SELECT id FROM chained_b"), "1")
    client.query("USE probe;CREATE TABLE target(id INT PRIMARY KEY,n INT);INSERT INTO target VALUES(1,0),(2,0),(3,0)")
    for mutation in ["UPDATE target JOIN (b JOIN c ON b.id=c.id) ON target.id=b.id SET b.id=4",
                     "DELETE b FROM target JOIN (b JOIN c ON b.id=c.id) ON target.id=b.id"]:
        locked = subprocess.run([*client.process.args, "-e", "USE probe;LOCK TABLES target READ,b READ,c READ;" + mutation], capture_output=True, text=True, check=False)
        lock_error = re.search(r"ERROR (\d+) \((\w+)\)", locked.stderr)
        expect("grouped mutation requires write lock", (int(lock_error[1]), lock_error[2]) if lock_error else None, (1099, "HY000"))
    client.query("CREATE USER 'grouped_reader'@'localhost';GRANT SELECT ON probe.* TO 'grouped_reader'@'localhost';GRANT UPDATE,DELETE ON probe.target TO 'grouped_reader'@'localhost'")
    reader_args = ["-ugrouped_reader" if arg == "-uroot" else arg for arg in client.process.args]
    for code, mutation in [(1142, "UPDATE b SET id=4"),
                           (1143, "UPDATE target JOIN b ON target.id=b.id SET b.id=4"),
                           (1143, "UPDATE target JOIN (b JOIN c ON b.id=c.id) ON target.id=b.id SET b.id=4"),
                     (1142, "DELETE b FROM target JOIN (b JOIN c ON b.id=c.id) ON target.id=b.id")]:
        for statement in [mutation, "PREPARE denied_group FROM '" + mutation + "'"]:
            denied = subprocess.run([*reader_args, "-e", "USE probe;" + statement], capture_output=True, text=True, check=False)
            privilege_error = re.search(r"ERROR (\d+) \((\w+)\)", denied.stderr)
            expect("grouped target requires mutation privilege", (int(privilege_error[1]), privilege_error[2]) if privilege_error else None, (code, "42000"))
    for statement in ["UPDATE target t JOIN (b JOIN c ON b.id=c.id) ON t.id=b.id SET t.n=20",
                      "DELETE t FROM target t JOIN (b JOIN c ON b.id=c.id) ON t.id=b.id"]:
        plan = client.query("USE probe;EXPLAIN " + statement)
        expect("grouped mutation EXPLAIN tables", sorted(row.split("\t")[2] for row in plan.splitlines()), ["b", "c", "t"])
    expect("EXPLAIN preserves targets", client.query("USE probe;SELECT id,n FROM target ORDER BY id"), "1\t0\n2\t0\n3\t0")
    client.query("USE probe;UPDATE target t LEFT JOIN (b JOIN c ON b.id=c.id) ON t.id=b.id SET t.n=IF(c.id IS NULL,10,20)")
    expect("grouped UPDATE null extension", client.query("USE probe;SELECT id,n FROM target ORDER BY id"), "1\t10\n2\t10\n3\t20")
    client.query("USE probe;DELETE t FROM target t JOIN (b JOIN c ON b.id=c.id) ON t.id=b.id")
    expect("grouped DELETE identities", client.query("USE probe;SELECT id,n FROM target ORDER BY id"), "1\t10\n2\t10")
    client.query("USE probe;UPDATE target t JOIN (b JOIN c ON b.id=c.id) ON t.id=1 SET b.id=4")
    expect("grouped inner UPDATE target", client.query("USE probe;SELECT id FROM b ORDER BY id"), "1\n4")
    client.query("USE probe;DELETE b FROM target t JOIN (b JOIN c ON b.id=c.id+1) ON t.id=1")
    expect("grouped inner DELETE target", client.query("USE probe;SELECT id FROM b ORDER BY id"), "1")
    plan = client.query("USE probe;EXPLAIN SELECT a.id FROM a LEFT JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id")
    expect("grouped EXPLAIN tables", sorted(row.split("\t")[2] for row in plan.splitlines()), ["a", "b", "c"])
    client.query("CREATE DATABASE grouped_target;CREATE TABLE grouped_target.items(id INT,n INT);INSERT INTO grouped_target.items VALUES(3,0)")
    client.query("USE probe;CREATE TRIGGER grouped_write AFTER INSERT ON a FOR EACH ROW UPDATE c JOIN (grouped_target.items d JOIN target t ON t.id=1) ON c.id=d.id SET d.n=NEW.id")
    client.query("USE probe;INSERT INTO a VALUES(9)")
    expect("trigger writes grouped cross-database target", client.query("SELECT n FROM grouped_target.items"), "9")
    query = "SELECT a.id FROM a LEFT JOIN (b JOIN c ON c.id=a.id) ON a.id=b.id"
    result = subprocess.run([*client.process.args, "-e", "USE probe;" + query], capture_output=True, text=True, check=False)
    match = re.search(r"ERROR (\d+) \((\w+)\)", result.stderr)
    expect("inner ON cannot see the outer left operand", (int(match[1]), match[2]) if match else None, (1054, "42S22"))


if __name__ == "__main__":
    oracle["run"](verify)
