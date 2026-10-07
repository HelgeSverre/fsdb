"""Check ngram posting preservation across ALTER TABLE on native MySQL 8.4.11."""

import pathlib
import shutil
import subprocess
import tempfile
import time


def run():
    data = pathlib.Path(tempfile.mkdtemp(prefix="fsdb-ngram-ddl-"))
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

    cases = [
        ("comment", "COMMENT='changed'"),
        ("add_check", "ADD CONSTRAINT added CHECK(extra>=0)"),
        ("visibility", "ALTER INDEX existing INVISIBLE"),
        ("comment_copy", "COMMENT='changed', ALGORITHM=COPY"),
        ("rename_column", "RENAME COLUMN body TO renamed_body"),
        ("add_ft", "ADD FULLTEXT KEY second(body) WITH PARSER ngram"),
        ("rowformat", "ROW_FORMAT=DYNAMIC"),
        ("rename_index", "RENAME INDEX ft TO renamed"),
        ("rename_over_dropped", "DROP INDEX second, RENAME INDEX ft TO second"),
        ("add_index", "ADD INDEX extra_id(extra)"),
        ("drop_index", "DROP INDEX existing"),
        ("add_column", "ADD COLUMN added INT"),
        ("drop_column", "DROP COLUMN extra"),
        ("modify_body", "MODIFY body MEDIUMTEXT"),
        ("default", "ALTER COLUMN extra SET DEFAULT 3"),
        ("auto_increment", "AUTO_INCREMENT=20"),
        ("engine", "ENGINE=InnoDB"),
        ("replace_ft", "DROP INDEX ft, ADD FULLTEXT KEY ft(body) WITH PARSER ngram"),
        ("rename_table", "RENAME TO probe.renamed_table"),
    ]
    try:
        with log_path.open("w") as log:
            subprocess.run(["mysqld", "--no-defaults", "--initialize-insecure", "--datadir=" + str(data), "--innodb-redo-log-capacity=64M"], check=True, stdout=log, stderr=subprocess.STDOUT)
            start(2, log)
            sql("CREATE DATABASE probe CHARACTER SET utf8mb4")
            for name, action in cases:
                sql(f"CREATE TABLE probe.{name}(id INT PRIMARY KEY AUTO_INCREMENT, extra INT DEFAULT 0, body TEXT, KEY existing(extra), FULLTEXT KEY ft(body) WITH PARSER ngram)")
                sql(f"INSERT INTO probe.{name}(id,body) VALUES(1,'生日快乐')")
            stop()
            start(3, log)
            for name, action in cases:
                sql(f"INSERT INTO probe.{name}(id,body) VALUES(2,'生日快乐')")
                if name == "rename_over_dropped":
                    sql(f"ALTER TABLE probe.{name} ADD FULLTEXT KEY second(body) WITH PARSER ngram")
                sql(f"ALTER TABLE probe.{name} {action}")
                table = "renamed_table" if name == "rename_table" else name
                column = "renamed_body" if name == "rename_column" else "body"
                result = sql(f"SELECT (SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.{table} WHERE MATCH({column}) AGAINST('生日' IN BOOLEAN MODE)), (SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.{table} WHERE MATCH({column}) AGAINST('生日快' IN BOOLEAN MODE))")
                rebuilt = name in {"add_check", "comment_copy", "rowformat", "add_column", "drop_column", "modify_body", "engine"}
                expected = "NULL\t1,2" if rebuilt else "1\t2"
                assert result == expected, (name, expected, result)
                print(name, result, flush=True)
                if name == "add_ft":
                    sql("ALTER TABLE probe.add_ft DROP INDEX ft")
                    check("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.add_ft WHERE MATCH(body) AGAINST('生日快' IN BOOLEAN MODE)", "1,2")
    except Exception:
        print(log_path.read_text(), flush=True)
        raise
    finally:
        stop()
        shutil.rmtree(data)
        log_path.unlink(missing_ok=True)

if __name__ == "__main__":
    run()
