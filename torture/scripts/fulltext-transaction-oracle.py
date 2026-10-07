"""Verify full-text transaction visibility on disposable native MySQL 8.4.11."""

import contextlib
import math
import os
import pathlib
import select
import subprocess
import tempfile
import time


class Client:
    def __init__(self, arguments):
        self.process = subprocess.Popen(
            ["mysql", "--no-defaults", "--default-character-set=utf8mb4",
             *arguments, "-uroot", "--batch", "--raw", "--skip-column-names",
             "--unbuffered"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, bufsize=0,
        )
        self.pending = b""

    def query(self, statement):
        self.process.stdin.write((statement + ";SELECT '__oracle_end__';\n").encode())
        lines = []
        deadline = time.monotonic() + 15
        while True:
            while b"\n" in self.pending:
                line, self.pending = self.pending.split(b"\n", 1)
                if line == b"__oracle_end__":
                    return b"\n".join(lines).decode()
                lines.append(line)
            remaining = deadline - time.monotonic()
            if remaining <= 0 or not select.select([self.process.stdout], [], [], remaining)[0]:
                raise TimeoutError(statement)
            chunk = os.read(self.process.stdout.fileno(), 65536)
            if not chunk:
                raise RuntimeError(self.process.stderr.read().decode())
            self.pending += chunk

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=10)
        self.process.stdout.close()
        self.process.stderr.close()


def expect(label, actual, expected):
    if actual != expected:
        raise AssertionError(f"{label}: expected {expected!r}, got {actual!r}")
    print(f"{label}: {actual!r}", flush=True)


def matches(term, column="body"):
    return ("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs "
            f"WHERE MATCH({column}) AGAINST('{term}' IN BOOLEAN MODE)")


def setup(client, old="orchard", new="cobalt", parser=""):
    client.query("DROP TABLE IF EXISTS probe.docs;"
                 "CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT,"
                 f"FULLTEXT KEY ft(body){parser});"
                 f"INSERT INTO probe.docs VALUES(1,'{old} red'),(2,'{old} blue'),(3,'{new} green')")


def own_writes(client):
    for label, old, new, parser in [
        ("words", "orchard", "cobalt", ""),
        ("ngram", "生日", "中文", " WITH PARSER ngram"),
    ]:
        for name, change, ending, expected in [
            ("no-op", "UPDATE probe.docs SET body=body WHERE id=1", "ROLLBACK", "1,2,3\n1,2\n3\n1,2\n3"),
            ("insert rollback", f"INSERT INTO probe.docs VALUES(4,'{old} gold')", "ROLLBACK", "1,2,3,4\n1,2\n3\n1,2\n3"),
            ("insert commit", f"INSERT INTO probe.docs VALUES(4,'{old} gold')", "COMMIT", "1,2,3,4\n1,2\n3\n1,2,4\n3"),
            ("update rollback", f"UPDATE probe.docs SET body='{new} orange' WHERE id=1", "ROLLBACK", "1,2,3\n2\n3\n1,2\n3"),
            ("update same term", f"UPDATE probe.docs SET body='{old} orange' WHERE id=1", "ROLLBACK", "1,2,3\n2\n3\n1,2\n3"),
            ("restore original text rollback", f"UPDATE probe.docs SET body='{new} orange' WHERE id=1;UPDATE probe.docs SET body='{old} red' WHERE id=1", "ROLLBACK", "1,2,3\n2\n3\n1,2\n3"),
            ("restore original text commit", f"UPDATE probe.docs SET body='{new} orange' WHERE id=1;UPDATE probe.docs SET body='{old} red' WHERE id=1", "COMMIT", "1,2,3\n2\n3\n1,2\n3"),
            ("update commit", f"UPDATE probe.docs SET body='{new} orange' WHERE id=1", "COMMIT", "1,2,3\n2\n3\n2\n1,3"),
            ("delete rollback", "DELETE FROM probe.docs WHERE id=1", "ROLLBACK", "2,3\n2\n3\n1,2\n3"),
        ]:
            setup(client, old, new, parser)
            statement = ("START TRANSACTION;" + change
                         + ";SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs;"
                         + matches(old) + ";" + matches(new) + ";" + ending
                         + ";" + matches(old) + ";" + matches(new))
            expect(f"{label} {name}", client.query(statement), expected)

        setup(client, old, new, parser)
        expect(label + " inserted-row projection", client.query(
            f"START TRANSACTION;INSERT INTO probe.docs VALUES(4,'{old} gold');"
            f"SELECT MATCH(body) AGAINST('{old}' IN BOOLEAN MODE)>0 FROM probe.docs WHERE id=4;ROLLBACK"), "0")
        expect(label + " updated-row projection", client.query(
            f"START TRANSACTION;UPDATE probe.docs SET body='{new} orange' WHERE id=1;"
            f"SELECT MATCH(body) AGAINST('{old}' IN BOOLEAN MODE)>0, MATCH(body) AGAINST('{new}' IN BOOLEAN MODE)>0 FROM probe.docs WHERE id=1;ROLLBACK"), "0\t0")

        setup(client, old, new, parser)
        expect(label + " savepoint", client.query(
            "START TRANSACTION;SAVEPOINT s;"
            f"UPDATE probe.docs SET body='{new} orange' WHERE id=1;"
            + matches(old) + ";ROLLBACK TO s;" + matches(old) + ";ROLLBACK"), "2\n1,2")

        for name, change, expected in [
            ("update predicate", f"UPDATE probe.docs SET id=id+10 WHERE MATCH(body) AGAINST('{old}' IN BOOLEAN MODE)", "1,3,12"),
            ("delete predicate", f"DELETE FROM probe.docs WHERE MATCH(body) AGAINST('{new}' IN BOOLEAN MODE)", "1,2"),
        ]:
            setup(client, old, new, parser)
            expect(label + " " + name, client.query(
                f"START TRANSACTION;UPDATE probe.docs SET body='{new} orange' WHERE id=1;"
                + change + ";SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs;ROLLBACK"), expected)

        client.query("ALTER TABLE probe.docs ADD COLUMN other TEXT, ADD COLUMN extra INT DEFAULT 0;"
                     "UPDATE probe.docs SET other=IF(id=1,'forest','ocean');"
                     "ALTER TABLE probe.docs ADD FULLTEXT KEY other_ft(other)")
        for change, expected in [
            ("UPDATE probe.docs SET body='modified' WHERE id=1", "NULL"),
            ("UPDATE probe.docs SET extra=1 WHERE id=1", "1"),
        ]:
            expect(label + " other index: " + change,
                   client.query("START TRANSACTION;" + change + ";" + matches("forest", "other") + ";ROLLBACK"), expected)


def concurrent_writes(reader, writer):
    ordinary = "SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs"
    for isolation in ["REPEATABLE READ", "READ COMMITTED"]:
        for name, change, repeatable, committed in [
            ("insert", "INSERT INTO probe.docs VALUES(4,'orchard gold')", "1,2,3\n1,2\n3", "1,2,3,4\n1,2,4\n3"),
            ("update", "UPDATE probe.docs SET body='cobalt orange' WHERE id=1", "1,2,3\n2\n3", "1,2,3\n2\n1,3"),
            ("delete", "DELETE FROM probe.docs WHERE id=1", "1,2,3\n2\n3", "2,3\n2\n3"),
            ("restored text", "START TRANSACTION;UPDATE probe.docs SET body='cobalt orange' WHERE id=1;UPDATE probe.docs SET body='orchard red' WHERE id=1;COMMIT", "1,2,3\n2\n3", "1,2,3\n1,2\n3"),
        ]:
            setup(writer)
            reader.query("SET TRANSACTION ISOLATION LEVEL " + isolation + ";START TRANSACTION;" + ordinary)
            writer.query(change)
            actual = reader.query(ordinary + ";" + matches("orchard") + ";" + matches("cobalt"))
            expect(isolation + " concurrent " + name, actual,
                   repeatable if isolation == "REPEATABLE READ" else committed)
            expected_body = "orchard red" if isolation == "REPEATABLE READ" or name in ["insert", "restored text"] else "cobalt orange" if name == "update" else "NULL"
            expect(isolation + " ordinary row after " + name,
                   reader.query("SELECT (SELECT body FROM probe.docs WHERE id=1)"), expected_body)
            reader.query("ROLLBACK")

    for name, change, expected in [
        ("insert", "INSERT INTO probe.docs VALUES(4,'orchard gold')", "1,2,3,4\n1,2\n3"),
        ("update", "UPDATE probe.docs SET body='cobalt orange' WHERE id=1", "1,2,3\n2\n3"),
        ("delete", "DELETE FROM probe.docs WHERE id=1", "2,3\n2\n3"),
    ]:
        setup(writer)
        writer.query("START TRANSACTION;" + change)
        reader.query("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;START TRANSACTION")
        expect("READ UNCOMMITTED pending " + name,
               reader.query(ordinary + ";" + matches("orchard") + ";" + matches("cobalt")), expected)
        reader.query("ROLLBACK")
        writer.query("ROLLBACK")


def relevance_population(reader, writer):
    def expect_score(client, statement, expected, label):
        actual = float(client.query(statement))
        if not math.isclose(actual, expected, rel_tol=1e-6, abs_tol=1e-12):
            raise AssertionError(f"{label}: expected {expected}, got {actual}")
        print(f"{label}: {actual}", flush=True)

    baseline = math.log10(3 / 2) ** 2
    for label, old, new, parser in [
        ("words", "orchard", "cobalt", ""),
        ("ngram", "生日", "中文", " WITH PARSER ngram"),
    ]:
        score = (f"SELECT MATCH(body) AGAINST('{old}' IN BOOLEAN MODE) "
                 "FROM probe.docs WHERE id=2")
        for isolation in ["REPEATABLE READ", "READ COMMITTED", "READ UNCOMMITTED"]:
            for name, change, pending_score in [
                ("insert", f"INSERT INTO probe.docs VALUES(4,'{new} gold')", math.log10(4 / 2) ** 2),
                ("delete", "DELETE FROM probe.docs WHERE id=1", math.log10(1.0001) ** 2),
            ]:
                setup(writer, old, new, parser)
                writer.query("ANALYZE TABLE probe.docs")
                reader.query("SET TRANSACTION ISOLATION LEVEL " + isolation
                             + ";START TRANSACTION;SELECT * FROM probe.docs")
                case = f"{label} {isolation} {name} relevance"
                expect_score(reader, score, baseline, case + " baseline")
                writer.query("START TRANSACTION;" + change)
                expect_score(reader, score, pending_score, case + " other pending")
                expect_score(writer, score, pending_score, case + " own pending")
                writer.query("ROLLBACK")
                expect("ordinary rows after rollback", reader.query("SELECT COUNT(*) FROM probe.docs"), "3")
                expect_score(reader, score, pending_score, case + " after rollback")
                writer.query("ANALYZE TABLE probe.docs")
                expect_score(reader, score, baseline, case + " after analyze")
                reader.query("ROLLBACK")


def run():
    with tempfile.TemporaryDirectory(prefix="fsdb-fulltext-transactions-") as directory:
        data = pathlib.Path(directory) / "data"
        data.mkdir()
        log_path = pathlib.Path(directory) / "server.log"
        socket = str(pathlib.Path(directory) / "mysql.sock")
        server = None
        try:
            with log_path.open("w") as log:
                subprocess.run(["mysqld", "--no-defaults", "--initialize-insecure",
                                "--datadir=" + str(data), "--innodb-redo-log-capacity=64M"],
                               stdout=log, stderr=subprocess.STDOUT, check=True)
                server = subprocess.Popen(
                    ["mysqld", "--no-defaults", "--datadir=" + str(data),
                     "--skip-networking", "--socket=" + socket, "--mysqlx=0",
                     "--innodb-buffer-pool-size=64M", "--innodb-redo-log-capacity=64M"],
                    stdout=log, stderr=subprocess.STDOUT,
                )
                for _ in range(100):
                    if server.poll() is not None:
                        raise RuntimeError("MySQL exited during startup")
                    ready = subprocess.run(["mysqladmin", "--no-defaults", "--socket=" + socket,
                                            "-uroot", "ping"], capture_output=True)
                    if ready.returncode == 0:
                        break
                    time.sleep(0.1)
                else:
                    raise TimeoutError("MySQL startup")
                with contextlib.closing(Client(["--socket=" + socket])) as reader, contextlib.closing(Client(["--socket=" + socket])) as writer:
                    expect("version", reader.query("SELECT VERSION()"), "8.4.11")
                    reader.query("CREATE DATABASE probe CHARACTER SET utf8mb4")
                    own_writes(reader)
                    concurrent_writes(reader, writer)
                    relevance_population(reader, writer)
        except Exception:
            print(log_path.read_text(), flush=True)
            raise
        finally:
            if server is not None and server.poll() is None:
                server.terminate()
                try:
                    server.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    server.kill()
                    server.wait(timeout=10)
    print("Full-text transaction oracle passed", flush=True)


if __name__ == "__main__":
    run()
