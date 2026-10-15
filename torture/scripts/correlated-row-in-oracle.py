"""Check correlated row-IN results on materialized MySQL 8.4.11 sources."""

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
    with tempfile.TemporaryDirectory(prefix="fsdb-correlated-row-in-") as directory:
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
                        "CREATE TABLE outer_rows(id INT PRIMARY KEY,a INT,b INT,c INT,d INT)",
                        "CREATE TABLE inner_rows(id INT PRIMARY KEY,x INT,y INT)",
                        "INSERT INTO outer_rows VALUES(1,1,10,2,20),(2,1,NULL,3,30),(3,NULL,10,2,20)",
                        "INSERT INTO inner_rows VALUES(1,1,10),(2,2,20),(3,3,30),(4,NULL,10)",
                    ]:
                        cursor.execute(sql)

                    sources = [
                        ("", "(SELECT id,x,y FROM inner_rows LIMIT 100) d"),
                        ("WITH materialized AS (SELECT id,x,y FROM inner_rows LIMIT 100) ", "materialized d"),
                    ]
                    for prefix, source in sources:
                        cursor.execute(
                            prefix + "SELECT o.id,(SELECT COUNT(*) FROM " + source
                            + " WHERE (d.x,d.y) IN ((o.a,o.b),(o.c,o.d)))"
                            + " FROM outer_rows o ORDER BY o.id"
                        )
                        assert cursor.fetchall() == ((1, 2), (2, 1), (3, 1))
                print(f"MySQL {version}: derived and CTE row-IN counts (2, 1, 1)")
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
