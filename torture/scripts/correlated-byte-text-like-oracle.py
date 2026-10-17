"""Check text-pattern LIKE against byte keys on native MySQL 8.4.11."""

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
    with tempfile.TemporaryDirectory(prefix="fsdb-correlated-byte-text-like-") as directory:
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
                        "CREATE TABLE patterns(id INT PRIMARY KEY,p VARCHAR(8))",
                        "CREATE TABLE codes(id INT PRIMARY KEY,code VARBINARY(8),KEY ix_code(code))",
                        "INSERT INTO patterns VALUES(1,'a01%'),(2,'a99%'),(3,NULL)",
                        "INSERT INTO codes VALUES(1,'a010'),(2,'a019'),(3,'a990'),(4,'b100'),(5,'a100')",
                    ]:
                        cursor.execute(sql)
                    for source in [
                        "codes c",
                        "(SELECT id,code FROM codes) c",
                        "(SELECT DISTINCT id,code FROM codes) c",
                    ]:
                        cursor.execute(
                            "SELECT p.id,(SELECT COUNT(*) FROM " + source
                            + " WHERE c.code LIKE p.p) FROM patterns p ORDER BY p.id"
                        )
                        assert cursor.fetchall() == ((1, 2), (2, 1), (3, 0))
                        cursor.execute(
                            "SELECT p.id,(SELECT COUNT(*) FROM " + source
                            + " WHERE c.code LIKE p.p OR c.id=1) FROM patterns p ORDER BY p.id"
                        )
                        assert cursor.fetchall() == ((1, 2), (2, 2), (3, 1))
                        cursor.execute(
                            "SELECT p.id,(SELECT COUNT(*) FROM " + source
                            + " WHERE c.code NOT LIKE p.p) FROM patterns p ORDER BY p.id"
                        )
                        assert cursor.fetchall() == ((1, 3), (2, 4), (3, 0))
                        cursor.execute(
                            "SELECT p.id,(SELECT COUNT(*) FROM " + source
                            + " WHERE c.code NOT LIKE p.p OR c.id=1) FROM patterns p ORDER BY p.id"
                        )
                        assert cursor.fetchall() == ((1, 4), (2, 4), (3, 1))
                print(f"MySQL {version}: text patterns against byte keys agree across three sources")
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
