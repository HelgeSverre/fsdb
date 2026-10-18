"""Compare two writable joined views under LEFT/RIGHT UPDATE joins with MySQL 8.4.11."""

import pathlib
import socket
import subprocess
import tempfile
import time

import pymysql


REPO = pathlib.Path(__file__).resolve().parents[2]
FSDB = REPO / "src/Fsdb.Cli/bin/Release/net10.0/Fsdb.Cli.dll"


def connect(**options):
    for _ in range(100):
        try:
            return pymysql.connect(user="root", autocommit=True, **options)
        except pymysql.MySQLError:
            time.sleep(0.1)
    raise RuntimeError("database server did not become ready")


def execute(connection, sql):
    try:
        with connection.cursor() as cursor:
            cursor.execute(sql)
            if sql.startswith("SELECT"):
                return cursor.fetchall()
            return cursor.rowcount
    except pymysql.MySQLError as error:
        return "error", error.args


def main():
    if not FSDB.exists():
        raise RuntimeError(f"Build the Release CLI first: {FSDB}")
    with tempfile.TemporaryDirectory(prefix="fsdb-two-view-outer-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql-data"
        mysql_socket = str(root / "mysql.sock")
        mysql = fsdb = mysql_connection = fsdb_connection = None
        with (root / "mysql.log").open("w") as mysql_log, (root / "fsdb.log").open("w") as fsdb_log:
            try:
                subprocess.run(
                    ["mysqld", "--no-defaults", "--initialize-insecure", f"--datadir={data}",
                     "--innodb-redo-log-capacity=64M"],
                    check=True, stdout=mysql_log, stderr=subprocess.STDOUT,
                )
                mysql = subprocess.Popen(
                    ["mysqld", "--no-defaults", f"--datadir={data}", "--skip-networking",
                     f"--socket={mysql_socket}", f"--pid-file={root / 'mysql.pid'}",
                     "--mysqlx=0", "--innodb-buffer-pool-size=64M",
                     "--innodb-redo-log-capacity=64M"],
                    stdout=mysql_log, stderr=subprocess.STDOUT,
                )
                mysql_connection = connect(unix_socket=mysql_socket)
                assert execute(mysql_connection, "SELECT VERSION()")[0][0].startswith("8.4.11")
                with socket.socket() as reservation:
                    reservation.bind(("127.0.0.1", 0))
                    port = reservation.getsockname()[1]
                fsdb = subprocess.Popen(
                    ["dotnet", str(FSDB), "--port", str(port)],
                    stdout=fsdb_log, stderr=subprocess.STDOUT,
                )
                fsdb_connection = connect(host="127.0.0.1", port=port)
                connections = (mysql_connection, fsdb_connection)
                setup = [
                    "CREATE DATABASE probe", "USE probe",
                    "CREATE TABLE a(id INT PRIMARY KEY,n INT)",
                    "CREATE TABLE b(id INT PRIMARY KEY,n INT)",
                    "CREATE TABLE link(id INT PRIMARY KEY)",
                    "INSERT INTO a VALUES(1,10),(2,20)",
                    "INSERT INTO b VALUES(1,30),(3,40)",
                    "INSERT INTO link VALUES(1),(2),(3)",
                    "CREATE VIEW va AS SELECT t.id,t.n,l.id AS link_id FROM a t JOIN link l ON t.id=l.id",
                    "CREATE VIEW vb AS SELECT t.id,t.n,l.id AS link_id FROM b t JOIN link l ON t.id=l.id",
                    "CREATE VIEW va_complex AS SELECT t.id,t.n,q.link_id FROM a t "
                    "JOIN (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) r ON t.id=r.id "
                    "JOIN LATERAL (SELECT r.id AS link_id) q ON TRUE",
                    "CREATE VIEW vb_complex AS SELECT t.id,t.n,q.link_id FROM b t "
                    "JOIN (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) r ON t.id=r.id "
                    "JOIN LATERAL (SELECT r.id AS link_id) q ON TRUE",
                ]
                for sql in setup:
                    expected, actual = (execute(connection, sql) for connection in connections)
                    assert actual == expected, (sql, expected, actual)
                cases = [
                    "UPDATE va LEFT JOIN vb ON va.link_id=vb.link_id SET va.n=va.n+1,vb.n=vb.n+1 WHERE va.id=1",
                    "UPDATE va LEFT JOIN vb ON va.link_id=vb.link_id SET va.n=va.n+1,vb.n=vb.n+1 WHERE va.id=2",
                    "UPDATE va RIGHT JOIN vb ON va.link_id=vb.link_id SET va.n=va.n+1,vb.n=vb.n+1 WHERE vb.id=3",
                    "UPDATE vb RIGHT JOIN va ON va.link_id=vb.link_id SET va.n=va.n+1,vb.n=vb.n+1 WHERE va.id=2",
                    "UPDATE va_complex va LEFT JOIN vb_complex vb ON va.link_id=vb.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE va.id=1",
                    "UPDATE va_complex va LEFT JOIN vb_complex vb ON va.link_id=vb.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE va.id=2",
                    "UPDATE va_complex va RIGHT JOIN vb_complex vb ON va.link_id=vb.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE vb.id=3",
                    "UPDATE vb_complex vb RIGHT JOIN va_complex va ON va.link_id=vb.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE va.id=2",
                ]
                for sql in cases:
                    expected, actual = (execute(connection, sql) for connection in connections)
                    assert actual == expected, (sql, expected, actual)
                    for table in ("a", "b"):
                        query = f"SELECT id,n FROM {table} ORDER BY id"
                        expected, actual = (execute(connection, query) for connection in connections)
                        assert actual == expected, (sql, query, expected, actual)
                checked_view = (
                    "CREATE VIEW va_checked AS SELECT t.id,t.n,q.link_id FROM a t "
                    "JOIN (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) r ON t.id=r.id "
                    "JOIN LATERAL (SELECT r.id AS link_id) q ON TRUE "
                    "WHERE t.id=1 WITH CASCADED CHECK OPTION"
                )
                expected, actual = (execute(connection, checked_view) for connection in connections)
                assert actual == expected == 0, (checked_view, expected, actual)
                rejected = (
                    "UPDATE va_checked va LEFT JOIN vb_complex vb ON va.link_id=vb.link_id "
                    "SET va.id=4,vb.n=vb.n+1 WHERE va.id=1"
                )
                expected, actual = (execute(connection, rejected) for connection in connections)
                assert actual == expected and expected[0] == "error" and expected[1][0] == 1369, (
                    rejected, expected, actual
                )
                for table in ("a", "b"):
                    query = f"SELECT id,n FROM {table} ORDER BY id"
                    expected, actual = (execute(connection, query) for connection in connections)
                    assert actual == expected, (rejected, query, expected, actual)
                print(f"MySQL 8.4.11: {len(cases)} two-view outer UPDATE cases and atomic CHECK OPTION matched")
            finally:
                if fsdb_connection:
                    fsdb_connection.close()
                if mysql_connection:
                    mysql_connection.close()
                if fsdb and fsdb.poll() is None:
                    fsdb.terminate()
                    fsdb.wait(timeout=20)
                if mysql and mysql.poll() is None:
                    subprocess.run(
                        ["mysqladmin", "--no-defaults", f"--socket={mysql_socket}", "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    mysql.wait(timeout=20)


if __name__ == "__main__":
    main()
