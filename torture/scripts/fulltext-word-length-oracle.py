"""Verify full-text indexing, lookup, and restart word-length boundaries."""

import pathlib
import runpy
import subprocess
import tempfile

startup = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-stopword-startup-oracle.py")))
native, expect = startup["native"], startup["expect"]


def matches(client, term, mode=""):
    return client.query("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs "
                        "WHERE MATCH(body) AGAINST('" + term + "'" + mode + ")")


def short_queries(client, _writer):
    client.query("CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body));"
                 "INSERT INTO probe.docs VALUES(1,'ffi'),(2,'orchard'),(3,'zzzz'),(4,'ﬃ')")
    expect("collation equivalence", client.query(
        "SELECT _utf8mb4'ﬃ' COLLATE utf8mb4_0900_ai_ci = _utf8mb4'ffi'"), "1")
    for mode in ["", " IN BOOLEAN MODE", " WITH QUERY EXPANSION"]:
        for term, expected in [("ﬃ", "1"), ("ﬃ ﬃ", "1"), ("+ﬃ", "1"), ('"ﬃ"', "NULL"), ('"ﬃ orchard"', "2")]:
            expect(mode + " " + term, matches(client, term, mode), expected)


    for query, expected in [("ffi ffi", "0.09062"), ("ﬃ ﬃ", "0.09062"),
                            ("ffi ﬃ", "0.09062"), ("+ffi +ﬃ", "0.09062"), ("ﬃ ﬃ ﬃ", "0.01561")]:
        expect("Boolean repeated score " + query, client.query(
            "SELECT CAST(MATCH(body) AGAINST('" + query + "' IN BOOLEAN MODE) AS DECIMAL(12,5)) "
            "FROM probe.docs WHERE id=1"), expected)


def restart_bounds():
    with tempfile.TemporaryDirectory(prefix="fsdb-word-lengths-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        log_path = root / "server.log"
        try:
            with log_path.open("w") as log:
                subprocess.run(["mysqld", "--no-defaults", "--initialize-insecure",
                                "--datadir=" + str(data), "--innodb-redo-log-capacity=64M",
                                "--innodb-buffer-pool-size=64M"], stdout=log, stderr=subprocess.STDOUT, check=True)
                for stage, minimum, maximum in [("initial", 1, 84), ("restricted", 3, 10), ("restored", 1, 84)]:
                    options = [f"--innodb-ft-min-token-size={minimum}", f"--innodb-ft-max-token-size={maximum}"]
                    with startup["server"](data, str(root / "mysql.sock"), log, "OFF", options) as client:
                        expect("version", client.query("SELECT VERSION()"), "8.4.11")
                        expect(stage + " bounds", client.query(
                            "SELECT @@GLOBAL.innodb_ft_min_token_size,@@GLOBAL.innodb_ft_max_token_size"), f"{minimum}\t{maximum}")
                        if stage == "initial":
                            client.query("CREATE DATABASE probe;CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body));"
                                         "INSERT INTO probe.docs VALUES(1,'xy'),(2,'abcdefghijk'),(3,'orchard'),(4,'zzzz')")
                        elif stage == "restricted":
                            client.query("INSERT INTO probe.docs VALUES(5,'xy abcdefghijk orchard')")
                        for mode in ["", " IN BOOLEAN MODE"]:
                            for term, expected in [("xy", "1"), ("abcdefghijk", "NULL" if stage == "restricted" else "2"),
                                                   ("orchard", "3" if stage == "initial" else "3,5"), ("xy*", "1"),
                                                   ("abc*", "2" if mode else "NULL")]:
                                expect(stage + mode + " " + term, matches(client, term, mode), expected)
        except Exception:
            print(log_path.read_text(), flush=True)
            raise


if __name__ == "__main__":
    native["run"](short_queries)
    restart_bounds()
    print("Full-text word-length oracle passed", flush=True)
