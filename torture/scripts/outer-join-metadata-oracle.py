"""Verify outer-join result flags on disposable native MySQL 8.4.11."""

import pathlib
import re
import runpy
import subprocess

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))


def verify(client, _writer):
    client.query("USE probe;CREATE TABLE a(id INT PRIMARY KEY);"
                 "CREATE TABLE b(id INT PRIMARY KEY);CREATE TABLE c(id INT PRIMARY KEY);"
                 "INSERT INTO a VALUES(1)")
    arguments = [arg for arg in client.process.args if arg not in ["--batch", "--raw", "--skip-column-names"]]
    for query, expected in [
        ("SELECT a.id,b.id,c.id FROM a LEFT JOIN (b JOIN c USING(id)) ON a.id=b.id", [True, False, False]),
        ("SELECT * FROM a LEFT JOIN (b LEFT JOIN c USING(id)) ON a.id=b.id", [True, False]),
        ("SELECT * FROM a RIGHT JOIN (b LEFT JOIN c USING(id)) ON a.id=b.id", [False, True]),
        ("SELECT * FROM b RIGHT JOIN c USING(id)", [True]),
    ]:
        result = subprocess.run([*arguments, "--column-type-info", "-vvv", "-e", "USE probe;" + query],
                                capture_output=True, text=True, check=True)
        flags = [line.split() for line in re.findall(r"^Flags:\s*(.*)", result.stdout, re.MULTILINE)]
        oracle["expect"]("outer-join NOT_NULL flags", ["NOT_NULL" in column for column in flags], expected)
        oracle["expect"]("primary-key flags survive NULL extension", ["PRI_KEY" in column for column in flags], [True] * len(expected))


if __name__ == "__main__":
    oracle["run"](verify)
