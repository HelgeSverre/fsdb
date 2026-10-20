"""Compare two writable joined views under LEFT/RIGHT UPDATE joins with MySQL 8.4.11."""

import pathlib
import socket
import subprocess
import tempfile
import threading
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


def contended_update(owner, waiter_options, table, row_id, sql):
    waiter = connect(**waiter_options)
    outcome = {}
    try:
        assert execute(waiter, "USE probe") == 0
        assert execute(owner, "BEGIN") == 0
        assert execute(owner, f"UPDATE {table} SET n=n+10 WHERE id={row_id}") == 1
        assert execute(waiter, "BEGIN") == 0

        def run_waiter():
            outcome["affected"] = execute(waiter, sql)
            if not isinstance(outcome["affected"], tuple):
                outcome["commit"] = execute(waiter, "COMMIT")

        thread = threading.Thread(target=run_waiter, daemon=True)
        thread.start()
        thread.join(0.2)
        waited = thread.is_alive()
        assert execute(owner, "COMMIT") == 0
        thread.join(5)
        assert not thread.is_alive(), (sql, outcome)
        assert outcome["affected"] in (1, 2) and outcome["commit"] == 0, (sql, outcome)
        return waited, outcome["affected"]
    finally:
        waiter.close()


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
                    "CREATE VIEW vb_unique AS SELECT t.id,t.n AS bonus,l.id AS link_id "
                    "FROM b t JOIN link l ON t.id=l.id",
                    "CREATE VIEW read_counts AS SELECT id,COUNT(*) AS hits FROM link GROUP BY id",
                    "CREATE VIEW va_complex AS SELECT t.id,t.n,q.link_id FROM a t "
                    "JOIN (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) r ON t.id=r.id "
                    "JOIN LATERAL (SELECT r.id AS link_id) q ON TRUE",
                    "CREATE VIEW vb_complex AS SELECT t.id,t.n,q.link_id FROM b t "
                    "JOIN (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) r ON t.id=r.id "
                    "JOIN LATERAL (SELECT r.id AS link_id) q ON TRUE",
                ]
                for sql in setup:
                    expected, actual = (execute(connection, sql) for connection in connections)
                    assert actual == expected and not isinstance(expected, tuple), (sql, expected, actual)
                cases = [
                    "WITH c AS (SELECT id FROM link) UPDATE c "
                    "JOIN LATERAL (SELECT c.id AS link_id) q ON TRUE "
                    "JOIN va ON q.link_id=va.link_id SET va.n=va.n+1 WHERE c.id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c "
                    "JOIN LATERAL (SELECT c.id AS link_id) q ON TRUE "
                    "JOIN va ON q.link_id=va.link_id "
                    "JOIN LATERAL (SELECT va.link_id AS next_id) r ON TRUE "
                    "JOIN vb ON r.next_id=vb.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE c.id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c "
                    "JOIN va ON c.id=va.link_id "
                    "JOIN JSON_TABLE(JSON_ARRAY(va.link_id),'$[*]' COLUMNS(next_id INT PATH '$')) r ON TRUE "
                    "JOIN vb ON r.next_id=vb.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE c.id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                    "JOIN LATERAL (SELECT va.link_id AS next_id FROM (SELECT 99 AS link_id) va) r ON TRUE "
                    "JOIN vb ON r.next_id=vb.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE c.id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                    "SET va.n=va.n+1 WHERE c.id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                    "JOIN vb ON va.link_id=vb.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE c.id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                    "LEFT JOIN vb ON va.link_id=vb.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE c.id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                    "LEFT JOIN vb ON va.link_id=vb.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE c.id=2",
                    "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                    "RIGHT JOIN vb ON va.link_id=vb.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE vb.id=3",
                    "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                    "LEFT JOIN vb ON va.link_id=vb.link_id "
                    "SET vb.n=vb.n+1 WHERE c.id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                    "LEFT JOIN vb ON va.link_id=vb.link_id "
                    "SET vb.n=vb.n+1 WHERE c.id=2",
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
                    "UPDATE va LEFT JOIN vb_unique vb ON va.link_id=vb.link_id "
                    "SET bonus=bonus+1 WHERE vb.id=1",
                    "UPDATE read_counts rc JOIN vb_unique vb ON rc.id=vb.link_id "
                    "SET bonus=bonus+1 WHERE vb.id=1",
                    "UPDATE read_counts rc JOIN va ON rc.id=va.link_id "
                    "LEFT JOIN vb ON va.link_id=vb.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE rc.id=1",
                    "UPDATE read_counts rc JOIN va ON rc.id=va.link_id "
                    "LEFT JOIN vb ON va.link_id=vb.link_id "
                    "SET vb.n=vb.n+1 WHERE rc.id=1",
                    "UPDATE (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) g "
                    "JOIN va ON g.id=va.link_id "
                    "JOIN LATERAL (SELECT l.id AS link_id,COUNT(*) AS hits FROM link l "
                    "WHERE l.id=g.id AND l.id=va.link_id GROUP BY l.id) q ON TRUE "
                    "JOIN vb ON vb.link_id=q.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE g.id=1",
                    "UPDATE (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) g "
                    "JOIN va ON g.id=va.link_id "
                    "JOIN LATERAL (SELECT MAX(l.id) AS link_id FROM link l "
                    "WHERE l.id=g.id AND l.id=va.link_id) q ON TRUE "
                    "LEFT JOIN vb ON vb.link_id=q.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE g.id=1",
                    "UPDATE (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) g "
                    "LEFT JOIN va ON g.id=va.link_id SET va.n=va.n+1 WHERE g.id=1",
                    "UPDATE (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) g "
                    "LEFT JOIN va ON g.id=va.link_id SET va.n=va.n+1 WHERE g.id=3",
                    "UPDATE (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) g "
                    "RIGHT JOIN va ON g.id=va.link_id SET va.n=va.n+1 WHERE va.id=2",
                    "UPDATE (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) g "
                    "LEFT JOIN va ON g.id=va.link_id LEFT JOIN vb ON vb.link_id=g.id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE g.id=1",
                    "UPDATE (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) g "
                    "RIGHT JOIN va ON g.id=va.link_id LEFT JOIN vb ON vb.link_id=va.link_id "
                    "SET va.n=va.n+1,vb.n=vb.n+1 WHERE va.id=1",
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
                contended = [
                    ("b", 1, "WITH c AS (SELECT id FROM link) UPDATE c "
                     "JOIN LATERAL (SELECT c.id AS link_id) q ON TRUE "
                     "JOIN va ON q.link_id=va.link_id "
                     "JOIN LATERAL (SELECT va.link_id AS next_id) r ON TRUE "
                     "JOIN vb ON r.next_id=vb.link_id "
                     "SET va.n=va.n+1,vb.n=vb.n+1 WHERE c.id=1"),
                    ("b", 1, "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                     "JOIN JSON_TABLE(JSON_ARRAY(va.link_id),'$[*]' COLUMNS(next_id INT PATH '$')) r ON TRUE "
                     "JOIN vb ON r.next_id=vb.link_id "
                     "SET va.n=va.n+1,vb.n=vb.n+1 WHERE c.id=1"),
                    ("a", 1, "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                     "SET va.n=va.n+1 WHERE c.id=1"),
                    ("a", 1, "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                     "JOIN vb ON va.link_id=vb.link_id "
                     "SET va.n=va.n+1,vb.n=vb.n+1 WHERE c.id=1"),
                    ("b", 1, "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                     "JOIN vb ON va.link_id=vb.link_id "
                     "SET va.n=va.n+1,vb.n=vb.n+1 WHERE c.id=1"),
                    ("a", 2, "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                     "LEFT JOIN vb ON va.link_id=vb.link_id "
                     "SET va.n=va.n+1,vb.n=vb.n+1 WHERE c.id=2"),
                    ("b", 1, "WITH c AS (SELECT id FROM link) UPDATE c JOIN va ON c.id=va.link_id "
                     "LEFT JOIN vb ON va.link_id=vb.link_id "
                     "SET vb.n=vb.n+1 WHERE c.id=1"),
                    ("a", 1, "UPDATE va_complex va LEFT JOIN vb_complex vb ON va.link_id=vb.link_id "
                     "SET va.n=va.n+1,vb.n=vb.n+1 WHERE va.id=1"),
                    ("a", 2, "UPDATE va_complex va LEFT JOIN vb_complex vb ON va.link_id=vb.link_id "
                     "SET va.n=va.n+1,vb.n=vb.n+1 WHERE va.id=2"),
                    ("b", 3, "UPDATE va_complex va RIGHT JOIN vb_complex vb ON va.link_id=vb.link_id "
                     "SET va.n=va.n+1,vb.n=vb.n+1 WHERE vb.id=3"),
                    ("b", 1, "UPDATE va LEFT JOIN vb_unique vb ON va.link_id=vb.link_id "
                     "SET bonus=bonus+1 WHERE vb.id=1"),
                    ("b", 1, "UPDATE read_counts rc JOIN vb_unique vb ON rc.id=vb.link_id "
                     "SET bonus=bonus+1 WHERE vb.id=1"),
                    ("a", 1, "UPDATE read_counts rc JOIN va ON rc.id=va.link_id "
                     "LEFT JOIN vb ON va.link_id=vb.link_id "
                     "SET va.n=va.n+1,vb.n=vb.n+1 WHERE rc.id=1"),
                    ("b", 1, "UPDATE read_counts rc JOIN va ON rc.id=va.link_id "
                     "LEFT JOIN vb ON va.link_id=vb.link_id "
                     "SET va.n=va.n+1,vb.n=vb.n+1 WHERE rc.id=1"),
                    ("b", 1, "UPDATE read_counts rc JOIN va ON rc.id=va.link_id "
                     "LEFT JOIN vb ON va.link_id=vb.link_id "
                     "SET vb.n=vb.n+1 WHERE rc.id=1"),
                    ("b", 1, "UPDATE (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) g "
                     "LEFT JOIN va ON g.id=va.link_id LEFT JOIN vb ON vb.link_id=g.id "
                     "SET va.n=va.n+1,vb.n=vb.n+1 WHERE g.id=1"),
                    ("a", 1, "UPDATE (SELECT id,COUNT(*) AS hits FROM link GROUP BY id) g "
                     "RIGHT JOIN va ON g.id=va.link_id LEFT JOIN vb ON vb.link_id=va.link_id "
                     "SET va.n=va.n+1,vb.n=vb.n+1 WHERE va.id=1"),
                ]
                for table, row_id, sql in contended:
                    expected = contended_update(mysql_connection, {"unix_socket": mysql_socket}, table, row_id, sql)
                    actual = contended_update(fsdb_connection, {"host": "127.0.0.1", "port": port}, table, row_id, sql)
                    assert expected == actual and expected[0], (sql, expected, actual)
                    for target in ("a", "b"):
                        query = f"SELECT id,n FROM {target} ORDER BY id"
                        expected_rows, actual_rows = (execute(connection, query) for connection in connections)
                        assert actual_rows == expected_rows, (sql, query, expected_rows, actual_rows)
                print(
                    f"MySQL 8.4.11: {len(cases)} two-view outer UPDATE cases, atomic CHECK OPTION, "
                    f"and {len(contended)} contended rebases matched"
                )
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
