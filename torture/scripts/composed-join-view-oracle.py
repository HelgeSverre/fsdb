"""Check complex inner-join view writes against native MySQL 8.4.11."""

import pathlib
import subprocess
import tempfile
import time

import pymysql


SHAPES = {
    "duplicate derived rows": "SELECT a.id,a.n,d1.flag FROM (SELECT id,flag FROM b LIMIT 100) d1 JOIN a ON a.id=d1.id",
    "middle aggregates": "SELECT a.id,a.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN a ON a.id=d1.id JOIN (SELECT id,SUM(aux) aux FROM c GROUP BY id) d2 ON d2.id=a.id",
    "middle JSON_TABLE": "SELECT a.id,a.n,d1.flag,j.x FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN a ON a.id=d1.id JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=a.id",
    "derived JSON_TABLE target": "SELECT a.id,a.n,d1.flag,j.x FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN JSON_TABLE(JSON_ARRAY(d1.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=d1.id JOIN a ON a.id=j.x",
    "grouped dependent JSON_TABLE": "SELECT a.id,a.n,d1.flag,j.x FROM ((SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN JSON_TABLE(JSON_ARRAY(d1.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=d1.id) JOIN a ON a.id=j.x",
    "middle lateral": "SELECT a.id,a.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN a ON a.id=d1.id JOIN LATERAL (SELECT SUM(aux) aux FROM c WHERE c.id=a.id) d2 ON TRUE",
    "aggregate lateral target": "SELECT a.id,a.n,d1.flag,e.extra FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN LATERAL (SELECT d1.flag+1 extra) e ON TRUE JOIN a ON a.id=d1.id",
    "aggregate lateral chain target": "SELECT a.id,a.n,d1.flag,e.extra,f.next_extra FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN LATERAL (SELECT d1.flag+1 extra) e ON TRUE JOIN LATERAL (SELECT e.extra+1 next_extra) f ON TRUE JOIN a ON a.id=d1.id",
    "aggregate lateral JSON target": "SELECT a.id,a.n,d1.flag,e.x,j.y FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN LATERAL (SELECT d1.id x) e ON TRUE JOIN JSON_TABLE(JSON_ARRAY(e.x),'$[*]' COLUMNS(y INT PATH '$')) j ON j.y=e.x JOIN a ON a.id=j.y",
    "aggregate JSON lateral target": "SELECT a.id,a.n,d1.flag,j.x,e.y FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN JSON_TABLE(JSON_ARRAY(d1.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=d1.id JOIN LATERAL (SELECT j.x y) e ON TRUE JOIN a ON a.id=e.y",
    "grouped aggregate lateral JSON target": "SELECT a.id,a.n,d1.flag,e.x,j.y FROM ((SELECT id,SUM(flag) flag FROM b GROUP BY id) d1 JOIN LATERAL (SELECT d1.id x) e ON TRUE JOIN JSON_TABLE(JSON_ARRAY(e.x),'$[*]' COLUMNS(y INT PATH '$')) j ON j.y=e.x) JOIN a ON a.id=j.y",
    "independent lateral first": "SELECT a.id,a.n,e.x FROM LATERAL (SELECT 1 x) e JOIN a ON a.id=e.x",
    "independent lateral JSON target": "SELECT a.id,a.n,e.x,j.y FROM LATERAL (SELECT 1 x) e JOIN JSON_TABLE(JSON_ARRAY(e.x),'$[*]' COLUMNS(y INT PATH '$')) j ON j.y=e.x JOIN a ON a.id=j.y",
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
                    "CREATE TABLE b(id INT,flag INT)",
                    "CREATE TABLE c(id INT PRIMARY KEY,aux INT)",
                    "INSERT INTO a VALUES(1,0),(2,0)",
                    "INSERT INTO b VALUES(1,7),(1,8)",
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

                    if name == "duplicate derived rows":
                        for first, second in [(7, 8), (8, 7)]:
                            run(connection, "DELETE FROM b")
                            run(connection, f"INSERT INTO b VALUES(1,{first}),(1,{second})")
                            run(connection, "CREATE VIEW vcp AS " + definition + " WHERE d1.flag > a.n WITH CASCADED CHECK OPTION")
                            assert_error(connection, "UPDATE vcp SET n=7 WHERE id=1", 1369)
                            assert run(connection, "SELECT id,n FROM a ORDER BY id") == ((1, 0), (2, 0))
                            run(connection, "DROP VIEW vcp")
                            run(connection, "CREATE VIEW vcj AS SELECT a.id,a.n,d1.flag FROM (SELECT id,flag FROM b LIMIT 100) d1 JOIN a ON a.id=d1.id AND d1.flag>a.n WITH CASCADED CHECK OPTION")
                            assert_error(connection, "UPDATE vcj SET n=7 WHERE id=1", 1369)
                            assert run(connection, "SELECT id,n FROM a ORDER BY id") == ((1, 0), (2, 0))
                            run(connection, "DROP VIEW vcj")

                run(connection, "CREATE TABLE insert_target(id INT PRIMARY KEY,n INT)")
                run(connection, "CREATE VIEW insert_checked AS SELECT z.id,z.n,b.flag FROM insert_target z JOIN b ON b.id=z.id WHERE b.flag>z.n WITH CASCADED CHECK OPTION")
                assert_error(connection, "INSERT INTO insert_checked(id,n) VALUES(1,7)", 1369)
                assert run(connection, "SELECT id,n FROM insert_target") == ()
                assert run(connection, "INSERT INTO insert_checked(id,n) VALUES(1,6)") == 1
                assert run(connection, "SELECT id,n FROM insert_target") == ((1, 6),)
                run(connection, "INSERT INTO b VALUES(2,7),(2,8)")
                run(connection, "CREATE VIEW insert_on_checked AS SELECT z.id,z.n,b.flag FROM insert_target z JOIN b ON b.id=z.id AND b.flag>z.n WITH CASCADED CHECK OPTION")
                assert run(connection, "INSERT INTO insert_on_checked(id,n) VALUES(2,7)") == 1
                assert run(connection, "SELECT id,n FROM insert_target ORDER BY id") == ((1, 6), (2, 7))

                for sql in [
                    "CREATE TABLE alias_target(id INT PRIMARY KEY,n INT)",
                    "CREATE TABLE alias_companion(id INT PRIMARY KEY,v INT)",
                    "CREATE TABLE alias_extra(id INT PRIMARY KEY,z INT)",
                    "INSERT INTO alias_target VALUES(1,10)",
                    "INSERT INTO alias_companion VALUES(1,1)",
                    "INSERT INTO alias_extra VALUES(1,1)",
                    "CREATE VIEW alias_inner AS SELECT t.id,t.n,a.v FROM alias_target t JOIN alias_companion a ON a.id=t.id WHERE t.n>a.v WITH CASCADED CHECK OPTION",
                    "CREATE VIEW alias_outer AS SELECT iv.id,iv.n,iv.v FROM alias_inner iv JOIN alias_extra a ON a.id=iv.id",
                ]:
                    run(connection, sql)
                assert run(connection, "SELECT IS_UPDATABLE FROM information_schema.VIEWS WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='alias_outer'") == (("YES",),)
                assert run(connection, "UPDATE alias_outer SET n=11 WHERE id=1") == 1
                assert run(connection, "UPDATE alias_outer SET v=2 WHERE id=1") == 1
                assert_error(connection, "UPDATE alias_outer SET v=12 WHERE id=1", 1369)
                assert run(connection, "SELECT n FROM alias_target") == ((11,),)
                assert run(connection, "SELECT v FROM alias_companion") == ((2,),)

                for sql in [
                    "CREATE TABLE two_source_target(id INT PRIMARY KEY,n INT)",
                    "CREATE TABLE two_source_aux(id INT PRIMARY KEY,v INT)",
                    "INSERT INTO two_source_target VALUES(1,10)",
                    "INSERT INTO two_source_aux VALUES(1,1)",
                    "CREATE VIEW two_source_inner AS SELECT t.id,t.n,a.v FROM two_source_target t JOIN two_source_aux a ON a.id=t.id WHERE t.n>a.v WITH CASCADED CHECK OPTION",
                    "CREATE VIEW two_source_outer AS SELECT iv.id,iv.n,x.k AS xk,y.k AS yk FROM (SELECT 1 AS k) x JOIN (SELECT 1 AS k) y ON y.k=x.k JOIN two_source_inner iv ON iv.id=x.k AND iv.id=y.k WITH CASCADED CHECK OPTION",
                ]:
                    run(connection, sql)
                assert run(connection, "SELECT IS_UPDATABLE FROM information_schema.VIEWS WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='two_source_outer'") == (("YES",),)
                assert run(connection, "UPDATE two_source_outer SET n=11 WHERE id=1") == 1
                assert_error(connection, "UPDATE two_source_outer SET n=0 WHERE id=1", 1369)
                assert run(connection, "SELECT n FROM two_source_target") == ((11,),)

                print(f"MySQL {version}: {len(SHAPES)} composed join-view shapes, private aliases, and two leading sources matched the expected write contract")
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
