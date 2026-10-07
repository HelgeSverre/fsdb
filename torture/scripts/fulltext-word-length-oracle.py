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


def startup_cases():
    return [("-1", 0, 10), ("0", 0, 10), ("1", 1, 10), ("10", 10, 10), ("16", 16, 16),
            ("100", 16, 84), ("1K", 16, 84), ("64MB", 16, 84), ("1T", 16, 84),
            ("1e2", 16, 84), ("K", 0, 10), ("-abc", 0, 10), (" 1", 1, 10),
            ("+1", 1, 10), ("18446744073709551615", 16, 84)]


def check_options():
    for value, minimum, maximum in startup_cases():
        result = subprocess.run(["mysqld", "--no-defaults", f"--innodb-ft-min-token-size={value}",
                                 f"--innodb-ft-max-token-size={value}", "--verbose", "--help"],
                                capture_output=True, text=True, check=True)
        assert "[ERROR]" not in result.stderr, result.stderr
        for name, expected in [("min", minimum), ("max", maximum)]:
            actual = next(line.split()[-1] for line in result.stdout.splitlines()
                          if line.startswith(f"innodb-ft-{name}-token-size "))
            expect(f"{name}={value}", actual, str(expected))

    for name in ["min", "max"]:
        for value in ["", "abc", "3.0", "1 ", "+", "16E", "9223372036854775807K", "18446744073709551616"]:
            result = subprocess.run(["mysqld", "--no-defaults", f"--innodb-ft-{name}-token-size={value}",
                                     "--verbose", "--help"], capture_output=True, text=True)
            # Help can exit successfully even when the plugin option parser reports an error.
            assert "[ERROR]" in result.stderr, (name, value, result.stderr)


def alteration_cases():
    return [("metadata", "ALTER TABLE probe.metadata COMMENT='words'", "1"),
            ("physical", "ALTER TABLE probe.physical ADD COLUMN extra INT", "NULL"),
            ("replacement", "ALTER TABLE probe.replacement DROP INDEX ft;ALTER TABLE probe.replacement ADD FULLTEXT ft(body)", "NULL"),
            ("other", "UPDATE probe.other SET other=2 WHERE id=1", "1"),
            ("changed", "UPDATE probe.changed SET body='xy orchard' WHERE id=1", "NULL")]


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
                        if stage == "initial":
                            client.query("CREATE TABLE probe.expansion(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body));"
                                         "INSERT INTO probe.expansion VALUES(1,'orchard xy abcdefghijk'),(2,'xy'),(3,'abcdefghijk'),(4,'zzzz')")
                        if stage == "restricted":
                            client.query("CREATE TABLE probe.narrow_seed(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body));"
                                         "INSERT INTO probe.narrow_seed VALUES(1,'orchard xy abcdefghijk'),(4,'zzzz')")
                        if stage == "restored":
                            client.query("INSERT INTO probe.narrow_seed VALUES(2,'xy'),(3,'abcdefghijk')")
                            expect("new seed words reach newer postings", client.query(
                                "SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.narrow_seed "
                                "WHERE MATCH(body) AGAINST('orchard' WITH QUERY EXPANSION)"), "1,2,3")
                        expect(stage + " expansion", client.query(
                            "SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.expansion "
                            "WHERE MATCH(body) AGAINST('orchard' WITH QUERY EXPANSION)"),
                            "1" if stage == "restricted" else "1,2,3")
                        for name, statement, expected in alteration_cases():
                            if stage == "initial":
                                client.query(f"CREATE TABLE probe.{name}(id INT PRIMARY KEY,other INT,body TEXT,FULLTEXT ft(body));"
                                             f"INSERT INTO probe.{name} VALUES(1,1,'xy'),(2,1,'zzzz')")
                            else:
                                if stage == "restricted":
                                    client.query(statement)
                                expect(stage + " " + name, client.query(
                                    f"SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.{name} "
                                    "WHERE MATCH(body) AGAINST('xy*' IN BOOLEAN MODE)"), expected)
                        for mode in ["", " IN BOOLEAN MODE"]:
                            for term, expected in [("xy", "1"), ("abcdefghijk", "NULL" if stage == "restricted" else "2"),
                                                   ("orchard", "3" if stage == "initial" else "3,5"), ("xy*", "1"),
                                                   ("abc*", "2" if mode else "NULL")]:
                                expect(stage + mode + " " + term, matches(client, term, mode), expected)
        except Exception:
            print(log_path.read_text(), flush=True)
            raise


if __name__ == "__main__":
    check_options()
    native["run"](short_queries)
    restart_bounds()
    print("Full-text word-length oracle passed", flush=True)
