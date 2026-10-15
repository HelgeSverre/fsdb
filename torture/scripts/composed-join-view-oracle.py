"""Check complex inner-join view writes against native MySQL 8.4.11."""

import pathlib
import subprocess
import tempfile
import time

import pymysql


SHAPES = {
    "middle aggregates": "SELECT a.id,a.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN a ON a.id=d1.id JOIN (SELECT id,SUM(aux) aux FROM c GROUP BY id) d2 ON d2.id=a.id",
    "middle JSON_TABLE": "SELECT a.id,a.n,d1.flag,j.x FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN a ON a.id=d1.id JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=a.id",
    "middle lateral": "SELECT a.id,a.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN a ON a.id=d1.id JOIN LATERAL (SELECT SUM(aux) aux FROM c WHERE c.id=a.id) d2 ON TRUE",
    "grouped read-only pair": "SELECT a.id,a.n,d1.flag,d2.aux FROM ((SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN (SELECT id,SUM(aux) aux FROM c GROUP BY id) d2 ON d2.id=d1.id) JOIN a ON a.id=d1.id",
    "grouped target pair": "SELECT a.id,a.n,d1.flag,d2.aux FROM ((SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN a ON a.id=d1.id) JOIN (SELECT id,SUM(aux) aux FROM c GROUP BY id) d2 ON d2.id=a.id",
    "target JSON_TABLE aggregate": "SELECT a.id,a.n,j.x,d1.flag FROM a JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=a.id JOIN (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 ON d1.id=j.x",
    "JSON_TABLE USING": "SELECT a.id,a.n,j.id AS jid FROM JSON_TABLE('[1]','$[*]' COLUMNS(id INT PATH '$')) j JOIN a USING(id)",
    "JSON_TABLE NATURAL": "SELECT a.id,a.n,j.id AS jid FROM JSON_TABLE('[1]','$[*]' COLUMNS(id INT PATH '$')) j NATURAL JOIN a",
    "aggregate NATURAL": "SELECT a.id,a.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 NATURAL JOIN a NATURAL JOIN (SELECT id,SUM(aux) aux FROM c GROUP BY id) d2",
    "aggregate USING": "SELECT a.id,a.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN a USING(id) JOIN (SELECT id,SUM(aux) aux FROM c GROUP BY id) d2 USING(id)",
}


def connect(socket):
    for _ in range(100):
        try:
            return pymysql.connect(unix_socket=socket, user="root", autocommit=True)
        except pymysql.MySQLError:
            time.sleep(0.1)
    raise RuntimeError("MySQL did not become ready")


def run(connection, sql):
    with connection.cursor() as cursor:
        cursor.execute(sql)
        return cursor.fetchall() if cursor.description else cursor.rowcount


def assert_error(connection, sql, code):
    try:
        run(connection, sql)
    except pymysql.MySQLError as error:
        assert error.args[0] == code, (sql, error)
    else:
        raise AssertionError(f"Expected MySQL error {code}: {sql}")


def main():
    with tempfile.TemporaryDirectory(prefix="fsdb-composed-view-oracle-") as directory:
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
                version = run(connection, "SELECT VERSION()")[0][0]
                assert version.startswith("8.4.11"), version
                for sql in [
                    "CREATE DATABASE probe", "USE probe",
                    "CREATE TABLE a(id INT PRIMARY KEY,n INT)",
                    "CREATE TABLE b(id INT PRIMARY KEY,flag INT)",
                    "CREATE TABLE c(id INT PRIMARY KEY,aux INT)",
                    "INSERT INTO a VALUES(1,0),(2,0)",
                    "INSERT INTO b VALUES(1,7)",
                    "INSERT INTO c VALUES(1,9)",
                ]:
                    run(connection, sql)

                for name, definition in SHAPES.items():
                    run(connection, "UPDATE a SET n=0")
                    run(connection, "CREATE VIEW v AS " + definition)
                    assert run(connection, "SELECT IS_UPDATABLE FROM information_schema.VIEWS WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='v'") == (("YES",),), name
                    assert run(connection, "UPDATE v SET n=n+1 WHERE id=1") == 1, name
                    assert run(connection, "SELECT id,n FROM a ORDER BY id") == ((1, 1), (2, 0)), name
                    assert_error(connection, "INSERT INTO v(id,n) VALUES(3,0)", 1471)
                    assert_error(connection, "DELETE FROM v WHERE id=1", 1395)
                    run(connection, "DROP VIEW v")

                    run(connection, "UPDATE a SET n=0")
                    run(connection, "CREATE VIEW vc AS " + definition + " WHERE a.n >= 0 WITH CASCADED CHECK OPTION")
                    assert run(connection, "SELECT IS_UPDATABLE FROM information_schema.VIEWS WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='vc'") == (("YES",),), name
                    assert_error(connection, "UPDATE vc SET n=-1 WHERE id=1", 1369)
                    assert run(connection, "SELECT id,n FROM a ORDER BY id") == ((1, 0), (2, 0)), name
                    run(connection, "DROP VIEW vc")

                print(f"MySQL {version}: {len(SHAPES)} composed join-view shapes matched the expected write contract")
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
