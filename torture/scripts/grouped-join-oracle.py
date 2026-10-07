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
    for source, expected in [
        ("a LEFT JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id", "1\tNULL\tNULL\n2\tNULL\tNULL"),
        ("(a LEFT JOIN b ON a.id=b.id) JOIN c ON b.id=c.id", ""),
        ("a JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id", ""),
        ("a LEFT JOIN (b,c) ON a.id=b.id", "1\t1\t3\n2\tNULL\tNULL"),
        ("a LEFT JOIN (b LEFT JOIN c ON b.id=c.id) ON a.id=b.id", "1\t1\tNULL\n2\tNULL\tNULL"),
    ]:
        query = "SELECT a.id,b.id,c.id FROM " + source + " ORDER BY a.id,b.id,c.id"
        expect(source, client.query("USE probe;" + query), expected)
    query = "SELECT a.id FROM a LEFT JOIN (b JOIN c ON c.id=a.id) ON a.id=b.id"
    result = subprocess.run([*client.process.args, "-e", "USE probe;" + query], capture_output=True, text=True, check=False)
    match = re.search(r"ERROR (\d+) \((\w+)\)", result.stderr)
    expect("inner ON cannot see the outer left operand", (int(match[1]), match[2]) if match else None, (1054, "42S22"))


if __name__ == "__main__":
    oracle["run"](verify)
