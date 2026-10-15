"""Compare joined view writes through independent CTE-derived sources with MySQL 8.4."""

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
    with tempfile.TemporaryDirectory(prefix="fsdb-derived-cte-view-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql-data"
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
                    for sql in [
                        "CREATE DATABASE probe", "USE probe",
                        "CREATE TABLE target(id INT PRIMARY KEY,n INT)",
                        "CREATE TABLE source(id INT,flag INT)",
                        "INSERT INTO target VALUES(1,10),(2,20)",
                        "INSERT INTO source VALUES(1,1),(1,2),(2,3)",
                    ]:
                        cursor.execute(sql)

                    for name, body in [
                        ("simple", "SELECT x.id,x.flag FROM (SELECT id,flag FROM source) x"),
                        ("aggregate", "SELECT x.id,SUM(x.flag) AS flag FROM (SELECT id,flag FROM source) x GROUP BY x.id"),
                        ("nested", "SELECT x.id,x.flag FROM (SELECT y.id,y.flag FROM (SELECT id,flag FROM source) y) x"),
                    ]:
                        view = f"derived_cte_{name}"
                        cursor.execute(
                            f"CREATE VIEW {view} AS WITH d(k,f) AS ({body}) "
                            "SELECT a.id,a.n,d.f FROM d JOIN target a ON a.id=d.k "
                            "WHERE a.n>=0 WITH CASCADED CHECK OPTION"
                        )
                        cursor.execute(
                            "SELECT IS_UPDATABLE FROM information_schema.views "
                            "WHERE table_schema='probe' AND table_name=%s", (view,)
                        )
                        assert cursor.fetchall() == (("YES",),), view
                        cursor.execute(f"UPDATE {view} SET n=n+1 WHERE id=1")
                        assert cursor.rowcount == 1, (view, cursor.rowcount)

                        for statement, error_code in [
                            (f"UPDATE {view} SET n=-1 WHERE id=1", 1369),
                            (f"INSERT INTO {view}(id,n) VALUES(3,0)", 1471),
                        ]:
                            try:
                                cursor.execute(statement)
                            except pymysql.MySQLError as error:
                                assert error.args[0] == error_code, (statement, error.args)
                            else:
                                raise AssertionError(f"expected {error_code}: {statement}")

                    cursor.execute("SELECT id,n FROM target ORDER BY id")
                    assert cursor.fetchall() == ((1, 13), (2, 20))
                print(f"MySQL {version}: nested derived CTE views remain updatable")
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
