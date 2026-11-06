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

                execute(owner, "CREATE TABLE chain_source(id INT,flag INT)")
                execute(owner, "INSERT INTO chain_source VALUES(1,7),(1,8)")
                execute(
                    owner,
                    "CREATE VIEW chain_view AS WITH ranked AS (SELECT id,ROW_NUMBER() OVER (PARTITION BY id ORDER BY flag) AS flag FROM chain_source), filtered AS (SELECT DISTINCT id,flag FROM ranked) SELECT a.id,a.n,filtered.flag FROM filtered JOIN a ON a.id=filtered.id WHERE a.n>=0 WITH CASCADED CHECK OPTION",
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "UPDATE chain_view SET n=n+1 WHERE id=1",
                    ((1, 2), (2, 0)),
                )
                execute(owner, "UPDATE a SET n=0 WHERE id=1")

                execute(owner, "CREATE TABLE source_lock(id INT PRIMARY KEY,flag INT)")
                execute(owner, "INSERT INTO source_lock VALUES(1,1)")
                execute(
                    owner,
                    "CREATE VIEW source_lock_view AS SELECT a.id,a.n,s.flag FROM a JOIN source_lock s ON s.id=a.id",
                )
                execute(owner, "BEGIN")
                assert execute(owner, "UPDATE a SET n=n+1 WHERE id=1") == 1
                assert execute(owner, "UPDATE source_lock SET flag=0 WHERE id=1") == 1
                execute(waiter, "BEGIN")
                source_result = []
                source_errors = []

                def update_after_source_change():
                    try:
                        source_result.append(
                            execute(
                                waiter,
                                "UPDATE source_lock_view SET n=n+1 WHERE id=1 AND flag=1",
                            )
                        )
                    except pymysql.MySQLError as error:
                        source_errors.append(error)

                source_wait = threading.Thread(target=update_after_source_change)
                source_wait.start()
                time.sleep(0.2)
                assert source_wait.is_alive(), "joined writer did not wait for target/source"
                execute(owner, "COMMIT")
                source_wait.join(timeout=5)
                assert not source_wait.is_alive(), "joined writer did not resume"
                assert not source_errors, source_errors
                assert source_result == [0], source_result
                execute(waiter, "COMMIT")
                assert execute(owner, "SELECT id,n FROM a WHERE id=1") == ((1, 1),)
                execute(owner, "UPDATE a SET n=0 WHERE id=1")
                execute(owner, "UPDATE source_lock SET flag=1 WHERE id=1")
                execute(owner, "BEGIN")
                assert execute(owner, "UPDATE source_lock SET flag=0 WHERE id=1") == 1
                execute(waiter, "BEGIN")
                source_result.clear()
                source_errors.clear()
                source_wait = threading.Thread(target=update_after_source_change)
                source_wait.start()
                time.sleep(0.2)
                source_only_waited = source_wait.is_alive()
                execute(owner, "COMMIT")
                source_wait.join(timeout=5)
                assert not source_wait.is_alive(), "source-only joined writer did not finish"
                assert not source_errors, source_errors
                assert source_only_waited, "joined writer did not wait for source row"
                assert source_result == [0], source_result
                execute(waiter, "COMMIT")
                execute(owner, "UPDATE source_lock SET flag=1 WHERE id=1")
                execute(owner, "INSERT INTO source_lock VALUES(2,1)")
                execute(owner, "BEGIN")
                assert execute(owner, "UPDATE source_lock SET flag=0 WHERE id=1") == 1
                execute(waiter, "BEGIN")
                disjoint_result = []

                def update_disjoint_source():
                    disjoint_result.append(
                        execute(
                            waiter,
                            "UPDATE source_lock_view SET n=n+1 WHERE id=2 AND flag=1",
                        )
                    )

                disjoint_wait = threading.Thread(target=update_disjoint_source)
                disjoint_wait.start()
                disjoint_wait.join(timeout=1)
                disjoint_source_waited = disjoint_wait.is_alive()
                execute(owner, "COMMIT")
                disjoint_wait.join(timeout=5)
                assert not disjoint_wait.is_alive(), "disjoint joined writer did not finish"
                assert not disjoint_source_waited, "disjoint joined writer waited for row 1"
                assert disjoint_result == [1], disjoint_result
                execute(waiter, "COMMIT")
                assert execute(owner, "SELECT id,n FROM a ORDER BY id") == ((1, 0), (2, 1))
                execute(owner, "UPDATE a SET n=0 WHERE id=2")
                execute(owner, "UPDATE source_lock SET flag=1 WHERE id=1")
                execute(owner, "CREATE TABLE view_outer(id INT PRIMARY KEY)")
                execute(owner, "INSERT INTO view_outer VALUES(1),(2)")
                execute(owner, "BEGIN")
                assert execute(owner, "UPDATE source_lock SET flag=0 WHERE id=1") == 1
                execute(waiter, "BEGIN")
                outer_result = []

                def update_outer_joined_view():
                    outer_result.append(
                        execute(
                            waiter,
                            "UPDATE source_lock_view v JOIN view_outer x ON x.id=v.id SET v.n=v.n+1 WHERE v.id=1 AND v.flag=1",
                        )
                    )

                outer_wait = threading.Thread(target=update_outer_joined_view)
                outer_wait.start()
                time.sleep(0.2)
                assert outer_wait.is_alive(), "outer-joined view writer did not wait for source"
                execute(owner, "COMMIT")
                outer_wait.join(timeout=5)
                assert not outer_wait.is_alive(), "outer-joined view writer did not resume"
                assert outer_result == [0], outer_result
                execute(waiter, "COMMIT")
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((0,),)
                execute(owner, "UPDATE source_lock SET flag=1 WHERE id=1")
                execute(owner, "BEGIN")
                assert execute(owner, "UPDATE source_lock SET flag=0 WHERE id=1") == 1
                execute(waiter, "BEGIN")
                outer_result.clear()

                def update_disjoint_outer_joined_view():
                    outer_result.append(
                        execute(
                            waiter,
                            "UPDATE source_lock_view v JOIN view_outer x ON x.id=v.id SET v.n=v.n+1 WHERE v.id=2 AND v.flag=1",
                        )
                    )

                outer_wait = threading.Thread(target=update_disjoint_outer_joined_view)
                outer_wait.start()
                outer_wait.join(timeout=1)
                assert not outer_wait.is_alive(), "disjoint outer-joined view writer waited"
                assert outer_result == [1], outer_result
                execute(owner, "COMMIT")
                execute(waiter, "COMMIT")
                assert execute(owner, "SELECT n FROM a WHERE id=2") == ((1,),)
                execute(owner, "UPDATE a SET n=0 WHERE id=2")
                execute(owner, "UPDATE source_lock SET flag=1 WHERE id=1")
                execute(owner, "BEGIN")
                assert execute(owner, "UPDATE source_lock SET flag=0 WHERE id=1") == 1
                execute(waiter, "BEGIN")
                outer_result.clear()

                def update_view_after_outer_source():
                    outer_result.append(
                        execute(
                            waiter,
                            "UPDATE view_outer x JOIN source_lock_view v ON v.id=x.id SET v.n=v.n+1 WHERE v.id=1 AND v.flag=1",
                        )
                    )

                outer_wait = threading.Thread(target=update_view_after_outer_source)
                outer_wait.start()
                time.sleep(0.2)
                assert outer_wait.is_alive(), "read-first joined view writer did not wait"
                execute(owner, "COMMIT")
                outer_wait.join(timeout=5)
                assert not outer_wait.is_alive(), "read-first joined view writer did not resume"
                assert outer_result == [0], outer_result
                execute(waiter, "COMMIT")
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((0,),)
                execute(owner, "UPDATE source_lock SET flag=1 WHERE id=1")

                execute(owner, "BEGIN")
                assert execute(owner, "UPDATE source_lock SET flag=0 WHERE id=1") == 1
                source_result.clear()
                source_errors.clear()
                source_wait = threading.Thread(target=update_after_source_change)
                source_wait.start()
                time.sleep(0.2)
                assert source_wait.is_alive(), "autocommit view writer did not wait for source"
                execute(owner, "COMMIT")
                source_wait.join(timeout=5)
                assert not source_wait.is_alive(), "autocommit view writer did not resume"
                assert not source_errors, source_errors
                assert source_result == [0], source_result
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((0,),)
                execute(owner, "UPDATE source_lock SET flag=1 WHERE id=1")

                execute(owner, "CREATE TABLE second_target(id INT PRIMARY KEY,n INT)")
                execute(owner, "CREATE TABLE second_source(id INT PRIMARY KEY,flag INT)")
                execute(owner, "INSERT INTO second_target VALUES(1,0)")
                execute(owner, "INSERT INTO second_source VALUES(1,1)")
                execute(
                    owner,
                    "CREATE VIEW second_view AS SELECT t.id,t.n,s.flag FROM second_target t JOIN second_source s ON s.id=t.id",
                )
                execute(owner, "BEGIN")
                assert execute(owner, "UPDATE second_source SET flag=0 WHERE id=1") == 1
                execute(waiter, "BEGIN")
                second_result = []
                second_errors = []

                def update_two_joined_views():
                    try:
                        second_result.append(
                            execute(
                                waiter,
                                "UPDATE source_lock_view v JOIN second_view w ON w.id=v.id SET v.n=v.n+1,w.n=w.n+1 WHERE v.id=1 AND w.flag=1",
                            )
                        )
                    except pymysql.MySQLError as error:
                        second_errors.append(error)

                second_wait = threading.Thread(target=update_two_joined_views)
                second_wait.start()
                time.sleep(0.2)
                assert second_wait.is_alive(), "second joined-view source did not delay writer"
                execute(owner, "COMMIT")
                second_wait.join(timeout=5)
                assert not second_wait.is_alive(), "second joined-view writer did not resume"
                assert not second_errors, second_errors
                assert second_result == [0], second_result
                execute(waiter, "COMMIT")
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((0,),)
                assert execute(owner, "SELECT n FROM second_target WHERE id=1") == ((0,),)
                execute(owner, "INSERT INTO second_target VALUES(2,0)")
                execute(owner, "INSERT INTO second_source VALUES(2,1)")
                execute(owner, "UPDATE second_source SET flag=1 WHERE id=1")
                execute(owner, "BEGIN")
                assert execute(owner, "UPDATE second_source SET flag=0 WHERE id=1") == 1
                execute(waiter, "BEGIN")
                second_result.clear()

                def update_disjoint_joined_views():
                    second_result.append(
                        execute(
                            waiter,
                            "UPDATE source_lock_view v JOIN second_view w ON w.id=v.id SET v.n=v.n+1,w.n=w.n+1 WHERE v.id=2 AND w.flag=1",
                        )
                    )

                second_wait = threading.Thread(target=update_disjoint_joined_views)
                second_wait.start()
                second_wait.join(timeout=1)
                assert not second_wait.is_alive(), "disjoint second-view writer waited"
                assert second_result == [2], second_result
                execute(owner, "COMMIT")
                execute(waiter, "COMMIT")
                assert execute(owner, "SELECT n FROM a WHERE id=2") == ((1,),)
                assert execute(owner, "SELECT n FROM second_target WHERE id=2") == ((1,),)
                execute(owner, "UPDATE a SET n=0 WHERE id=2")

                execute(owner, "CREATE TABLE source_link(id INT PRIMARY KEY)")
                execute(owner, "INSERT INTO source_link VALUES(1),(2)")
                for view_name, join_sql in (
                    ("using_source_view", "JOIN source_lock s USING(id)"),
                    ("natural_source_view", "NATURAL JOIN source_lock s"),
                    (
                        "chained_source_view",
                        "JOIN source_link l ON l.id=a.id JOIN source_lock s ON s.id=l.id",
                    ),
                    (
                        "using_chained_source_view",
                        "JOIN source_link l USING(id) JOIN source_lock s ON s.id=l.id",
                    ),
                    (
                        "natural_chained_source_view",
                        "NATURAL JOIN source_link l JOIN source_lock s ON s.id=l.id",
                    ),
                ):
                    execute(
                        owner,
                        f"CREATE VIEW {view_name} AS SELECT a.id,a.n,s.flag FROM a {join_sql}",
                    )
                    execute(owner, "UPDATE source_lock SET flag=1 WHERE id=1")
                    execute(owner, "BEGIN")
                    assert (
                        execute(owner, "UPDATE source_lock SET flag=0 WHERE id=1") == 1
                    )
                    execute(waiter, "BEGIN")
                    view_result = []

                    def update_view(
                        row_id, view_name=view_name, view_result=view_result
                    ):
                        view_result.append(
                            execute(
                                waiter,
                                f"UPDATE {view_name} SET n=n+1 WHERE id={row_id} AND flag=1",
                            )
                        )

                    view_wait = threading.Thread(target=update_view, args=(1,))
                    view_wait.start()
                    time.sleep(0.2)
                    assert view_wait.is_alive(), (
                        f"{view_name} writer did not wait for source row"
                    )
                    execute(owner, "COMMIT")
                    view_wait.join(timeout=5)
                    assert not view_wait.is_alive(), (
                        f"{view_name} writer did not resume"
                    )
                    assert view_result == [0], view_result
                    execute(waiter, "COMMIT")
                    execute(owner, "UPDATE source_lock SET flag=1 WHERE id=1")
                    execute(owner, "BEGIN")
                    assert (
                        execute(owner, "UPDATE source_lock SET flag=0 WHERE id=1") == 1
                    )
                    execute(waiter, "BEGIN")
                    view_result.clear()
                    view_wait = threading.Thread(target=update_view, args=(2,))
                    view_wait.start()
                    view_wait.join(timeout=1)
                    assert not view_wait.is_alive(), (
                        f"disjoint {view_name} writer waited for row 1"
                    )
                    assert view_result == [1], view_result
                    execute(owner, "COMMIT")
                    execute(waiter, "COMMIT")
                    assert execute(owner, "SELECT n FROM a WHERE id=2") == ((1,),)
                    execute(owner, "UPDATE a SET n=0 WHERE id=2")

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
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 46),),
                )
                assert execute(
                    owner,
                    "WITH c AS (SELECT 2 AS id) UPDATE c NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=2",
                ) == 0
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 48),),
                )
                assert execute(
                    owner,
                    "WITH c AS (SELECT 2 AS id) UPDATE c NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1",
                ) == 1
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((49,),)
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c JOIN tail x ON x.k=c.id NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 51),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c JOIN tail x ON x.k=c.id NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 53),),
                )
                execute(owner, "INSERT INTO tail VALUES(2)")
                assert execute(
                    owner,
                    "WITH c AS (SELECT 2 AS id) UPDATE c JOIN tail x ON x.k=c.id NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=2",
                ) == 0
                assert execute(
                    owner,
                    "WITH c AS (SELECT 2 AS id) UPDATE c JOIN tail x ON x.k=c.id NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1",
                ) == 1
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((54,),)
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c LEFT JOIN tail x ON x.k=c.id NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 56),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c LEFT JOIN tail x ON x.k=c.id NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 58),),
                )
                assert execute(
                    owner,
                    "WITH c AS (SELECT 3 AS id) UPDATE c LEFT JOIN tail x ON x.k=c.id NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=3",
                ) == 0
                assert execute(
                    owner,
                    "WITH c AS (SELECT 3 AS id) UPDATE c LEFT JOIN tail x ON x.k=c.id NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1",
                ) == 1
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((59,),)
                execute(owner, "CREATE TABLE nullable_tail(k INT PRIMARY KEY,link_id INT)")
                execute(owner, "INSERT INTO nullable_tail VALUES(1,1)")
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c LEFT JOIN nullable_tail x ON x.k=c.id NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 61),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c LEFT JOIN nullable_tail x ON x.k=c.id NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 63),),
                )
                execute(owner, "DELETE FROM nullable_tail WHERE k=1")
                assert execute(
                    owner,
                    "WITH c AS (SELECT id FROM link) UPDATE c LEFT JOIN nullable_tail x ON x.k=c.id NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=1",
                ) == 0
                assert execute(
                    owner,
                    "WITH c AS (SELECT id FROM link) UPDATE c LEFT JOIN nullable_tail x ON x.k=c.id NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1",
                ) == 1
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((64,),)
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c RIGHT JOIN tail x ON x.k=c.id NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 66),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT id FROM link) UPDATE c RIGHT JOIN tail x ON x.k=c.id NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 68),),
                )
                assert execute(
                    owner,
                    "WITH c AS (SELECT 3 AS id) UPDATE c RIGHT JOIN tail x ON x.k=c.id NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=1",
                ) == 0
                assert execute(
                    owner,
                    "WITH c AS (SELECT 3 AS id) UPDATE c RIGHT JOIN tail x ON x.k=c.id NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1",
                ) == 1
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((69,),)
                execute(owner, "CREATE TABLE mixed_tail(k INT PRIMARY KEY,link_id INT)")
                execute(owner, "INSERT INTO mixed_tail VALUES(1,1),(2,2)")
                mixed_prefix = (
                    "WITH c AS (SELECT id FROM link) "
                    "UPDATE c LEFT JOIN tail x ON x.k=c.id "
                    "RIGHT JOIN mixed_tail y ON y.k=c.id "
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    mixed_prefix + "NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 71),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    mixed_prefix + "NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 73),),
                )
                execute(owner, "DELETE FROM mixed_tail WHERE k=1")
                assert execute(owner, mixed_prefix + "NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=1") == 0
                assert execute(owner, mixed_prefix + "NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1") == 1
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((74,),)
                execute(owner, "CREATE TABLE using_tail(id INT PRIMARY KEY,tag INT)")
                execute(owner, "INSERT INTO using_tail VALUES(1,0),(2,0)")
                for prefix, expected in (
                    ("JOIN", ((1, 76),)),
                    ("LEFT JOIN", ((1, 78),)),
                    ("RIGHT JOIN", ((1, 80),)),
                ):
                    verify_wait(
                        owner,
                        waiter,
                        "UPDATE a SET n=n+1 WHERE id=1",
                        f"WITH c AS (SELECT id FROM link) UPDATE c {prefix} using_tail x USING(id) NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=1",
                        expected,
                    )
                assert execute(
                    owner,
                    "WITH c AS (SELECT 3 AS id) UPDATE c LEFT JOIN using_tail x USING(id) NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=3",
                ) == 0
                assert execute(
                    owner,
                    "WITH c AS (SELECT 3 AS id) UPDATE c LEFT JOIN using_tail x USING(id) NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1",
                ) == 1
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((81,),)
                assert execute(
                    owner,
                    "WITH c AS (SELECT 2 AS id) UPDATE c JOIN using_tail x USING(id) NATURAL LEFT JOIN v SET x.tag=x.tag+1,v.n=v.n+1 WHERE id=2",
                ) == 1
                assert execute(owner, "SELECT tag FROM using_tail WHERE id=2") == ((1,),)
                execute(owner, "CREATE TABLE using_next(id INT PRIMARY KEY,flag INT)")
                execute(owner, "INSERT INTO using_next VALUES(1,0),(2,0)")
                using_chain = (
                    "WITH c AS (SELECT id FROM link) UPDATE c "
                    "LEFT JOIN using_tail x USING(id) RIGHT JOIN using_next y USING(id) "
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    using_chain + "NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 83),),
                )
                verify_wait(
                    owner,
                    waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    using_chain + "NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1",
                    ((1, 85),),
                )
                execute(owner, "DELETE FROM using_next WHERE id=1")
                assert execute(owner, using_chain + "NATURAL LEFT JOIN v SET v.n=v.n+1 WHERE id=1") == 0
                assert execute(owner, using_chain + "NATURAL RIGHT JOIN v SET v.n=v.n+1 WHERE id=1") == 1
                assert execute(owner, "SELECT n FROM a WHERE id=1") == ((86,),)
                assert execute(
                    owner,
                    using_chain + "NATURAL LEFT JOIN v SET y.flag=y.flag+1,v.n=v.n+1 WHERE id=2",
                ) == 1
                assert execute(owner, "SELECT flag FROM using_next WHERE id=2") == ((1,),)
                for direction in ("LEFT", "RIGHT"):
                    statement = (
                        "WITH c AS (SELECT id FROM link) "
                        "UPDATE c JOIN link l ON l.id=c.id "
                        f"NATURAL {direction} JOIN v SET v.n=v.n+1 WHERE id=1"
                    )
                    try:
                        execute(owner, statement)
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1052, error
                    else:
                        raise AssertionError(f"duplicate grouped natural {direction} join was accepted")
                print(
                    "MySQL 8.4.11: CTE, derived, and joined-view writes waited, rebased, and preserved disjoint source progress"
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
