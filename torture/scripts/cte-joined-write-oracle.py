"""Check CTE-joined write waits against a disposable native MySQL 8.4.11."""

import pathlib
import subprocess
import tempfile
import threading
import time

import pymysql


def connect(socket):
    for _ in range(100):
        try:
            return pymysql.connect(unix_socket=socket, user="root", autocommit=True)
        except pymysql.MySQLError:
            time.sleep(0.1)
    raise RuntimeError("MySQL did not become ready")


def execute(connection, sql):
    with connection.cursor() as cursor:
        cursor.execute(sql)
        return cursor.fetchall() if sql.startswith("SELECT") else cursor.rowcount


def verify_wait(owner, waiter, owner_sql, waiter_sql, expected_rows):
    execute(owner, "BEGIN")
    assert execute(owner, owner_sql) == 1
    execute(waiter, "BEGIN")
    result = []
    waiting = threading.Thread(
        target=lambda: result.append(execute(waiter, waiter_sql))
    )
    waiting.start()
    time.sleep(0.2)
    assert waiting.is_alive(), "joined writer did not wait for the target row"
    execute(owner, "COMMIT")
    waiting.join(timeout=5)
    assert not waiting.is_alive(), "joined writer did not resume"
    assert result == [1], result
    execute(waiter, "COMMIT")
    assert execute(owner, "SELECT id,n FROM probe.a ORDER BY id") == expected_rows


def main():
    with tempfile.TemporaryDirectory(prefix="fsdb-cte-write-oracle-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql-data"
        data.mkdir()
        socket = str(root / "mysql.sock")
        server = owner = waiter = None
        with (root / "mysql.log").open("w") as log:
            try:
                subprocess.run(
                    [
                        "mysqld",
                        "--no-defaults",
                        "--initialize-insecure",
                        f"--datadir={data}",
                        "--innodb-redo-log-capacity=64M",
                    ],
                    check=True,
                    stdout=log,
                    stderr=subprocess.STDOUT,
                )
                server = subprocess.Popen(
                    [
                        "mysqld",
                        "--no-defaults",
                        f"--datadir={data}",
                        "--skip-networking",
                        f"--socket={socket}",
                        f"--pid-file={root / 'mysql.pid'}",
                        "--mysqlx=0",
                        "--innodb-buffer-pool-size=64M",
                        "--innodb-redo-log-capacity=64M",
                    ],
                    stdout=log,
                    stderr=subprocess.STDOUT,
                )
                owner = connect(socket)
                waiter = connect(socket)
                version = execute(owner, "SELECT VERSION()")[0][0]
                assert version.startswith("8.4.11"), version
                execute(owner, "CREATE DATABASE probe")
                execute(owner, "CREATE TABLE probe.a(id INT PRIMARY KEY,n INT)")
                execute(owner, "INSERT INTO probe.a VALUES(1,0),(2,0)")
                execute(owner, "USE probe")
                execute(waiter, "USE probe")

                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT 1 AS id) UPDATE a JOIN c ON c.id=a.id SET a.n=a.n+1 WHERE a.id=1",
                    ((1, 2), (2, 0)),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT 1 AS id) UPDATE a JOIN c ON c.id=a.id SET n=n+1 WHERE a.id=1",
                    ((1, 4), (2, 0)),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=2",
                    "WITH c AS (SELECT 2 AS id) DELETE a FROM a JOIN c ON c.id=a.id WHERE a.id=2",
                    ((1, 4),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "UPDATE a JOIN (SELECT 1 AS id) d ON d.id=a.id SET n=n+1 WHERE a.id=1",
                    ((1, 6),),
                )
                execute(owner, "CREATE TABLE probe.link(id INT PRIMARY KEY)")
                execute(owner, "INSERT INTO probe.link VALUES(1)")
                execute(
                    owner,
                    "CREATE VIEW probe.v AS SELECT a.id,a.n,l.id AS link_id FROM probe.a a JOIN probe.link l ON a.id=l.id",
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c JOIN v ON c.id=v.link_id SET v.n=v.n+1 WHERE c.id=1",
                    ((1, 8),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c CROSS JOIN v SET v.n=v.n+1 WHERE c.id=v.link_id AND c.id=1",
                    ((1, 10),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c CROSS JOIN link l CROSS JOIN v SET v.n=v.n+1 WHERE c.id=l.id AND l.id=v.link_id AND c.id=1",
                    ((1, 12),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c STRAIGHT_JOIN v ON c.id=v.link_id SET v.n=v.n+1 WHERE c.id=1",
                    ((1, 14),),
                )
                execute(owner, "UPDATE a SET n=0 WHERE id=1")
                for first, second in ((10, 20), (20, 10)):
                    assert (
                        execute(
                            owner,
                            f"WITH c AS (SELECT 1 id,{first} x UNION ALL SELECT 1,{second}) UPDATE c STRAIGHT_JOIN v ON c.id=v.link_id SET v.n=c.x",
                        )
                        == 1
                    )
                    assert execute(owner, "SELECT n FROM a WHERE id=1") == ((first,),)
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN v SET v.n=v.n+1 WHERE c.id=1",
                    ((1, 22),),
                )
                assert (
                    execute(
                        owner,
                        "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN v SET v.n=v.n+1 WHERE id=1",
                    )
                    == 1
                )
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((23,),)
                execute(owner, "CREATE TABLE tail(k INT PRIMARY KEY)")
                execute(owner, "INSERT INTO tail VALUES(1)")
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN v JOIN tail x ON x.k=c.id SET v.n=v.n+1 WHERE id=1",
                    ((1, 25),),
                )
                try:
                    execute(
                        owner,
                        "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN v JOIN link l ON l.id=c.id SET v.n=v.n+1 WHERE id=1",
                    )
                except pymysql.MySQLError as error:
                    assert error.args[0] == 1052, error
                else:
                    raise AssertionError(
                        "a later id source did not make the bare filter ambiguous"
                    )
                assert (
                    execute(
                        owner,
                        "WITH c AS (SELECT id,-1 AS n FROM link) UPDATE c NATURAL JOIN v SET v.n=v.n+1 WHERE id=1",
                    )
                    == 0
                )
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((25,),)
                execute(owner, "CREATE TABLE natural_read(id INT PRIMARY KEY)")
                execute(owner, "INSERT INTO natural_read VALUES(1)")
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r JOIN v ON v.id=r.id SET v.n=v.n+1 WHERE c.id=1",
                    ((1, 27),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r NATURAL JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 29),),
                )
                execute(owner, "UPDATE natural_read SET id=2 WHERE id=1")
                assert (
                    execute(
                        owner,
                        "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r JOIN v ON v.id=r.id SET v.n=v.n+1 WHERE c.id=1",
                    )
                    == 0
                )
                assert (
                    execute(
                        owner,
                        "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r NATURAL JOIN v SET v.n=v.n+1 WHERE id=1",
                    )
                    == 0
                )
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((29,),)
                execute(owner, "UPDATE natural_read SET id=1 WHERE id=2")
                execute(owner, "CREATE TABLE natural_read_next(id INT PRIMARY KEY)")
                execute(owner, "INSERT INTO natural_read_next VALUES(1)")
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r NATURAL JOIN natural_read_next s JOIN v ON v.id=s.id SET v.n=v.n+1 WHERE c.id=1",
                    ((1, 31),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r NATURAL JOIN natural_read_next s NATURAL JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 33),),
                )
                execute(owner, "UPDATE natural_read_next SET id=2 WHERE id=1")
                assert (
                    execute(
                        owner,
                        "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r NATURAL JOIN natural_read_next s JOIN v ON v.id=s.id SET v.n=v.n+1 WHERE c.id=1",
                    )
                    == 0
                )
                assert (
                    execute(
                        owner,
                        "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r NATURAL JOIN natural_read_next s NATURAL JOIN v SET v.n=v.n+1 WHERE id=1",
                    )
                    == 0
                )
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((33,),)
                execute(owner, "CREATE TABLE natural_extra(id INT PRIMARY KEY,n INT)")
                execute(owner, "INSERT INTO natural_extra VALUES(1,33)")
                assert (
                    execute(
                        owner,
                        "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_extra r NATURAL JOIN v SET v.n=v.n+1 WHERE id=1",
                    )
                    == 1
                )
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((34,),)
                assert (
                    execute(
                        owner,
                        "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_extra r NATURAL JOIN v SET v.n=v.n+1 WHERE id=1",
                    )
                    == 0
                )
                execute(owner, "UPDATE natural_read_next SET id=1 WHERE id=2")
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r NATURAL JOIN v JOIN tail x ON x.k=c.id SET v.n=v.n+1 WHERE id=1",
                    ((1, 36),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r NATURAL JOIN natural_read_next s NATURAL JOIN v JOIN tail x ON x.k=c.id SET v.n=v.n+1 WHERE id=1",
                    ((1, 38),),
                )
                try:
                    execute(
                        owner,
                        "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r NATURAL JOIN v JOIN link l ON l.id=c.id SET v.n=v.n+1 WHERE id=1",
                    )
                except pymysql.MySQLError as error:
                    assert error.args[0] == 1052, error
                else:
                    raise AssertionError(
                        "a later id source did not make the merged id ambiguous"
                    )
                try:
                    execute(
                        owner,
                        "WITH c AS (SELECT 1 AS id,9 AS n) UPDATE a JOIN c ON c.id=a.id SET n=n+1 WHERE a.id=1",
                    )
                except pymysql.MySQLError as error:
                    assert error.args[0] == 1052, error
                else:
                    raise AssertionError("ambiguous CTE assignment was accepted")
                try:
                    execute(
                        owner,
                        "UPDATE a JOIN (SELECT 1 AS id,9 AS n) d ON d.id=a.id SET n=n+1 WHERE a.id=1",
                    )
                except pymysql.MySQLError as error:
                    assert error.args[0] == 1052, error
                else:
                    raise AssertionError("ambiguous derived assignment was accepted")
                execute(owner, "CREATE TABLE natural_tail(id INT PRIMARY KEY)")
                execute(owner, "INSERT INTO natural_tail VALUES(1)")
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN v NATURAL JOIN natural_tail t SET v.n=v.n+1 WHERE id=1",
                    ((1, 40),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r NATURAL JOIN v NATURAL JOIN natural_tail t SET v.n=v.n+1 WHERE id=1",
                    ((1, 42),),
                )
                execute(owner, "UPDATE natural_tail SET id=2 WHERE id=1")
                assert execute(
                    owner,
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r NATURAL JOIN v NATURAL JOIN natural_tail t SET v.n=v.n+1 WHERE id=1",
                ) == 0
                execute(owner, "UPDATE natural_tail SET id=1 WHERE id=2")
                try:
                    execute(
                        owner,
                        "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN natural_read r NATURAL JOIN v NATURAL JOIN natural_tail t JOIN link l ON l.id=c.id SET v.n=v.n+1 WHERE id=1",
                    )
                except pymysql.MySQLError as error:
                    assert error.args[0] == 1052, error
                else:
                    raise AssertionError("an ordinary source after the natural chain did not make id ambiguous")
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((42,),)
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN v JOIN tail x ON x.k=c.id NATURAL JOIN natural_tail t SET v.n=v.n+1 WHERE id=1",
                    ((1, 44),),
                )
                execute(owner, "UPDATE natural_tail SET id=2 WHERE id=1")
                assert execute(
                    owner,
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN v JOIN tail x ON x.k=c.id NATURAL JOIN natural_tail t SET v.n=v.n+1 WHERE id=1",
                ) == 0
                execute(owner, "UPDATE natural_tail SET id=1 WHERE id=2")
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((44,),)
                try:
                    execute(
                        owner,
                        "WITH c AS (SELECT id FROM link) UPDATE c NATURAL JOIN v JOIN link l ON l.id=c.id NATURAL JOIN natural_tail t SET v.n=v.n+1 WHERE id=1",
                    )
                except pymysql.MySQLError as error:
                    assert error.args[0] == 1052, error
                else:
                    raise AssertionError("a duplicate id before the later natural join was accepted")
                print(
                    "MySQL 8.4.11: CTE, derived, and joined-view writes (including a NATURAL join after an ordinary source) waited and committed"
                )
            except Exception:
                print((root / "mysql.log").read_text())
                raise
            finally:
                if waiter:
                    waiter.close()
                if owner:
                    owner.close()
                if server and server.poll() is None:
                    subprocess.run(
                        [
                            "mysqladmin",
                            "--no-defaults",
                            f"--socket={socket}",
                            "-uroot",
                            "shutdown",
                        ],
                        check=False,
                        stdout=subprocess.DEVNULL,
                        stderr=subprocess.DEVNULL,
                    )
                    server.wait(timeout=20)


if __name__ == "__main__":
    main()
