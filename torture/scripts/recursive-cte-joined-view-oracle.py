"""Check writable joins beside recursive CTE sources against native MySQL 8.4."""

import pathlib
import subprocess
import tempfile
import time

import pymysql


def connect(socket):
    for _ in range(100):
        try:
            return pymysql.connect(unix_socket=socket, user="root", autocommit=True)
        except pymysql.MySQLError:
            time.sleep(0.1)
    raise RuntimeError("MySQL did not become ready")


def main():
    with tempfile.TemporaryDirectory(prefix="fsdb-recursive-view-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        socket = str(root / "mysql.sock")
        server = connection = None

        with (root / "mysql.log").open("w") as log:
            try:
                subprocess.run(
                    ["mysqld", "--no-defaults", "--initialize-insecure", f"--datadir={data}",
                     "--innodb-redo-log-capacity=64M"],
                    check=True, stdout=log, stderr=subprocess.STDOUT,
                )
                server = subprocess.Popen(
                    ["mysqld", "--no-defaults", f"--datadir={data}", "--skip-networking",
                     f"--socket={socket}", f"--pid-file={root / 'mysql.pid'}", "--mysqlx=0",
                     "--innodb-buffer-pool-size=64M", "--innodb-redo-log-capacity=64M"],
                    stdout=log, stderr=subprocess.STDOUT,
                )
                connection = connect(socket)
                with connection.cursor() as cursor:
                    cursor.execute("SELECT VERSION()")
                    version = cursor.fetchone()[0]
                    assert version.startswith("8.4.11"), version
                    for query in [
                        "CREATE DATABASE probe", "USE probe",
                        "CREATE TABLE target(id INT PRIMARY KEY,n INT)",
                        "INSERT INTO target VALUES(1,10),(2,20),(3,30),(4,40)",
                    ]:
                        cursor.execute(query)

                    recursive = "WITH RECURSIVE seq(k) AS (SELECT 1 UNION ALL SELECT k+1 FROM seq WHERE k<3)"
                    cases = [
                        ("recursive_first", f"{recursive} SELECT t.id,t.n FROM seq JOIN target t ON t.id=seq.k"),
                        ("recursive_last", f"{recursive} SELECT t.id,t.n FROM target t JOIN seq ON t.id=seq.k"),
                        ("recursive_group", f"{recursive} SELECT t.id,t.n FROM (seq JOIN target t ON t.id=seq.k)"),
                        ("recursive_inferred",
                         "WITH RECURSIVE seq AS (SELECT 1 AS k UNION ALL SELECT k+1 FROM seq WHERE k<3) "
                         "SELECT t.id,t.n FROM seq JOIN target t ON t.id=seq.k"),
                    ]
                    for index, (name, body) in enumerate(cases, start=1):
                        cursor.execute(f"CREATE VIEW {name} AS {body}")
                        cursor.execute(
                            "SELECT IS_UPDATABLE FROM information_schema.views "
                            "WHERE table_schema='probe' AND table_name=%s", (name,),
                        )
                        assert cursor.fetchone() == ("YES",), name
                        cursor.execute(f"UPDATE {name} SET n=n+1 WHERE id=2")
                        assert cursor.rowcount == 1, name
                        cursor.execute("SELECT n FROM target WHERE id=2")
                        assert cursor.fetchone() == (20 + index,), name

                    cursor.execute(
                        f"CREATE VIEW recursive_checked AS {recursive} "
                        "SELECT t.id,t.n FROM seq JOIN target t ON t.id=seq.k "
                        "WHERE t.n>=0 WITH CASCADED CHECK OPTION"
                    )
                    try:
                        cursor.execute("UPDATE recursive_checked SET n=-1 WHERE id=2")
                        raise AssertionError("CHECK OPTION allowed an invalid recursive-view write")
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1369, error.args

                    cursor.execute(f"CREATE VIEW recursive_only AS {recursive} SELECT k FROM seq")
                    cursor.execute(
                        "SELECT IS_UPDATABLE FROM information_schema.views "
                        "WHERE table_schema='probe' AND table_name='recursive_only'"
                    )
                    assert cursor.fetchone() == ("YES",)  # MySQL metadata overstates this view.
                    try:
                        cursor.execute("UPDATE recursive_only SET k=4")
                        raise AssertionError("recursive-only view accepted UPDATE")
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1288, error.args

                    print(f"MySQL {version}: four recursive-source writes and two rejection cases match")
            finally:
                if connection:
                    connection.close()
                if server and server.poll() is None:
                    subprocess.run(
                        ["mysqladmin", "--no-defaults", f"--socket={socket}", "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    server.wait(timeout=20)


if __name__ == "__main__":
    main()
