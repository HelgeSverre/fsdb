"""Check prepared XA full-text recovery on disposable native MySQL 8.4.11."""

import pathlib
import shutil
import subprocess
import tempfile
import time


def probe(restart_size):
    data = pathlib.Path(tempfile.mkdtemp(prefix="fsdb-ngram-xa-"))
    socket = str(data / "mysql.sock")
    log_path = data.with_suffix(".log")
    server = None

    def sql(statement):
        return subprocess.check_output(
            ["mysql", "--no-defaults", "--default-character-set=utf8mb4",
             "--socket=" + socket, "-uroot", "--batch", "--raw",
             "--skip-column-names", "-e", statement], text=True,
        ).strip()

    def check(statement, expected):
        actual = sql(statement)
        if actual != expected:
            raise AssertionError(f"{statement}: expected {expected!r}, got {actual!r}")
        print(f"{statement} -> {actual}", flush=True)

    def stop():
        nonlocal server
        if server is not None and server.poll() is None:
            subprocess.run(
                ["mysqladmin", "--no-defaults", "--socket=" + socket,
                 "-uroot", "shutdown"], check=True, timeout=20,
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            )
            server.wait(timeout=20)
        server = None

    def start(size, log):
        nonlocal server
        server = subprocess.Popen(
            ["mysqld", "--no-defaults", "--datadir=" + str(data),
             "--skip-networking", "--socket=" + socket,
             "--pid-file=" + str(data / "mysql.pid"), "--mysqlx=0",
             "--innodb-buffer-pool-size=64M", "--innodb-redo-log-capacity=64M",
             "--ngram-token-size=" + str(size)],
            stdout=log, stderr=subprocess.STDOUT,
        )
        for _ in range(30):
            if server.poll() is not None:
                raise RuntimeError("MySQL exited during startup")
            ready = subprocess.run(
                ["mysqladmin", "--no-defaults", "--socket=" + socket,
                 "-uroot", "ping"], stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            if ready.returncode == 0:
                check("SELECT VERSION()", "8.4.11")
                check("SELECT @@ngram_token_size", str(size))
                return
            time.sleep(1)
        raise RuntimeError("MySQL did not become ready")

    def matches(term, mode="IN BOOLEAN MODE"):
        return ("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs "
                f"WHERE MATCH(body) AGAINST('{term}' {mode})")

    try:
        with log_path.open("w") as log:
            subprocess.run(
                ["mysqld", "--no-defaults", "--initialize-insecure",
                 "--datadir=" + str(data), "--innodb-redo-log-capacity=64M"],
                check=True, stdout=log, stderr=subprocess.STDOUT,
            )
            print(f"Prepared at size 2; restart at size {restart_size}", flush=True)
            start(2, log)
            sql("CREATE DATABASE probe CHARACTER SET utf8mb4")
            sql("CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT,"
                "FULLTEXT(body) WITH PARSER ngram)")
            sql("INSERT INTO probe.docs VALUES(1,'生日快乐')")
            check("XA START 'same_session'; INSERT INTO probe.docs VALUES(2,'生日快乐');"
                  "SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs;"
                  + matches("生日") + ";"
                  "XA END 'same_session'; XA PREPARE 'same_session'; XA COMMIT 'same_session'",
                  "1,2\n1")
            check(matches("生日"), "1,2")
            sql("XA START 'detached'; INSERT INTO probe.docs VALUES(3,'生日快乐');"
                "XA END 'detached'; XA PREPARE 'detached'")
            sql("XA COMMIT 'detached'")
            check(matches("生日"), "1,2,3")
            sql("XA START 'recovered'; INSERT INTO probe.docs VALUES(4,'生日快乐');"
                "XA END 'recovered'; XA PREPARE 'recovered'")
            stop()

            start(restart_size, log)
            check("XA RECOVER", "1\t9\t0\trecovered")
            check("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs", "1,2,3")
            sql("XA COMMIT 'recovered'")
            check("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs", "1,2,3,4")
            # Recovery commits the row but does not restore its pending FTS posting.
            check(matches("生日"), "1,2,3")
            sql("ANALYZE TABLE probe.docs")
            check(matches("生日"), "1,2,3")
            sql("INSERT INTO probe.docs VALUES(5,'生日快乐')")
            check(matches("生日快", "IN NATURAL LANGUAGE MODE"),
                  "1,2,3,5" if restart_size == 2 else "5")
            sql("ALTER TABLE probe.docs DROP INDEX body")
            sql("ALTER TABLE probe.docs ADD FULLTEXT(body) WITH PARSER ngram")
            check(matches("生日快", "IN NATURAL LANGUAGE MODE"), "1,2,3,4,5")
    except Exception:
        if log_path.exists():
            print(log_path.read_text(), flush=True)
        raise
    finally:
        stop()
        shutil.rmtree(data)
        log_path.unlink(missing_ok=True)
        print("Disposed native MySQL", flush=True)


if __name__ == "__main__":
    for size in (2, 3):
        probe(size)
