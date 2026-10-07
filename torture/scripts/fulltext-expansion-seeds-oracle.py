"""Verify InnoDB expansion seeds independently of the MyISAM expansion limit."""

import pathlib
import runpy
import subprocess
import tempfile

startup = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-stopword-startup-oracle.py")))
expect = startup["expect"]


def verify(client, limit):
    expect("version", client.query("SELECT VERSION()"), "8.4.11")
    expect("reported limit", client.query("SELECT @@GLOBAL.ft_query_expansion_limit"), str(limit))
    client.query("CREATE DATABASE IF NOT EXISTS probe")
    values = []
    for identifier in range(1, 26):
        word = "uniqueword" + chr(96 + identifier)
        values.extend([f"({identifier},'orchard {word}')", f"({100 + identifier},'{word}')"])
    values.append("(999,'unrelated')")
    for engine in ["InnoDB", "MyISAM"]:
        client.query("DROP TABLE IF EXISTS probe.docs;"
                     "CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body)) ENGINE=" + engine + ";"
                     "INSERT INTO probe.docs VALUES" + ",".join(values) + ";ANALYZE TABLE probe.docs")
        matches = client.query("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs "
                               "WHERE MATCH(body) AGAINST('orchard' WITH QUERY EXPANSION)")
        identifiers = [int(value) for value in matches.split(",")]
        if engine == "InnoDB":
            expect(engine + " seeds at limit " + str(limit), identifiers, list(range(1, 26)) + list(range(101, 126)))
        else:
            expect(engine + " direct matches at limit " + str(limit), [value for value in identifiers if value <= 25], list(range(1, 26)))
            expect(engine + " expanded count at limit " + str(limit), len(identifiers), 25 + min(limit, 25))


def run():
    with tempfile.TemporaryDirectory(prefix="fsdb-expansion-seeds-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        log_path = root / "server.log"
        try:
            with log_path.open("w") as log:
                subprocess.run(["mysqld", "--no-defaults", "--initialize-insecure",
                                "--datadir=" + str(data), "--innodb-redo-log-capacity=64M",
                                "--innodb-buffer-pool-size=64M"], stdout=log, stderr=subprocess.STDOUT, check=True)
                for limit in [0, 1, 2, 20, 1000]:
                    with startup["server"](data, str(root / "mysql.sock"), log, "ON",
                                           ["--ft-query-expansion-limit=" + str(limit)]) as client:
                        verify(client, limit)
        except Exception:
            print(log_path.read_text(), flush=True)
            raise


if __name__ == "__main__":
    run()
    print("Full-text expansion seed oracle passed", flush=True)
