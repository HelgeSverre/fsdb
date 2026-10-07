"""Verify ngram startup sizing and recovery against disposable native MySQL 8.4.11."""

import pathlib
import shutil
import subprocess
import tempfile
import time


def run():
    data = pathlib.Path(tempfile.mkdtemp(prefix="fsdb-ngram-size-"))
    socket = str(data / "mysql.sock")
    server = None
    log_path = data.with_suffix(".log")

    def sql(statement):
        return subprocess.check_output(
            ["mysql", "--no-defaults", "--default-character-set=utf8mb4",
             "--socket=" + socket, "-uroot", "--batch", "--raw",
             "--skip-column-names", "-e", statement],
            text=True,
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

    def matches(term, mode="IN NATURAL LANGUAGE MODE"):
        return (
            "SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs "
            f"WHERE MATCH(body) AGAINST('{term}' {mode})"
        )

    try:
        with log_path.open("w") as log:
            subprocess.run(
                ["mysqld", "--no-defaults", "--initialize-insecure",
                 "--datadir=" + str(data)],
                check=True, stdout=log, stderr=subprocess.STDOUT,
            )
            # Each restart retains postings made with the preceding token size.
            for requested, effective, new_id, rebuilt_rows in [
                (2, 2, 12, "1,2,12"),
                (3, 3, 13, "1,12,13"),
                (1, 1, 11, "1,2,3,11,12,13"),
                (10, 10, 20, "NULL"),
                (0, 1, 10, "1,2,3,10,11,12,13,20"),
                (11, 10, 21, "NULL"),
            ]:
                server = subprocess.Popen(
                    ["mysqld", "--no-defaults", "--datadir=" + str(data),
                     "--skip-networking", "--socket=" + socket,
                     "--pid-file=" + str(data / "mysql.pid"), "--mysqlx=0",
                     "--innodb-buffer-pool-size=64M",
                     "--ngram-token-size=" + str(requested)],
                    stdout=log, stderr=subprocess.STDOUT,
                )
                for _ in range(30):
                    if server.poll() is not None:
                        raise RuntimeError("MySQL exited during startup")
                    ready = subprocess.run(
                        ["mysqladmin", "--no-defaults", "--socket=" + socket,
                         "-uroot", "ping"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    if ready.returncode == 0:
                        break
                    time.sleep(1)
                else:
                    raise RuntimeError("MySQL did not become ready")

                print(f"Requested token size {requested}", flush=True)
                check("SELECT VERSION()", "8.4.11")
                check("SELECT @@ngram_token_size", str(effective))
                if requested == 2:
                    sql("CREATE DATABASE probe CHARACTER SET utf8mb4")
                    sql("CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT,"
                        "FULLTEXT(body) WITH PARSER ngram)")
                    sql("INSERT INTO probe.docs VALUES(1,'生日快乐'),(2,'生日'),"
                        "(3,'快乐'),(4,'abcdefghijk'),(5,'一二三四五六七八九十')")

                check(matches("生日快"), "1,2" if requested == 2 else "NULL")
                if requested == 3:
                    check(matches("生日", "IN BOOLEAN MODE"), "1,2,12")
                sql(f"INSERT INTO probe.docs VALUES({new_id},'生日快乐')")
                new_rows = "1,2,12" if requested == 2 else str(new_id)
                check(matches("生日快"), "NULL" if effective == 10 else new_rows)

                if requested == 3:
                    check(matches('"生日"', "IN BOOLEAN MODE"), "1,2,12")
                    check(matches("生*", "IN BOOLEAN MODE"), "1,2,12,13")
                    check("START TRANSACTION; DELETE FROM probe.docs WHERE id=2;"
                          + matches("生*", "IN BOOLEAN MODE") + ";ROLLBACK",
                          "1,12,13")

                # Separate statements force removal of the old full-text storage.
                sql("ALTER TABLE probe.docs DROP INDEX body")
                sql("ALTER TABLE probe.docs ADD FULLTEXT(body) WITH PARSER ngram")
                check(matches("生日快"), rebuilt_rows)
                if effective == 10:
                    check(matches("一二三四五六七八九十"), "5")
                stop()
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
    run()
