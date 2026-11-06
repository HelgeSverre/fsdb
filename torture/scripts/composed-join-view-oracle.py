"""Check complex inner-join view writes against native MySQL 8.4.11."""

import pathlib
import subprocess
import tempfile
import time

import pymysql

SHAPES = {
    "duplicate derived rows": "SELECT a.id,a.n,d1.flag FROM (SELECT id,flag FROM b LIMIT 100) d1 JOIN a ON a.id=d1.id",
    "distinct derived rows": "SELECT a.id,a.n,d1.flag FROM (SELECT DISTINCT id,flag FROM b) d1 JOIN a ON a.id=d1.id",
    "window derived rows": "SELECT a.id,a.n,d1.flag FROM (SELECT id,ROW_NUMBER() OVER (PARTITION BY id ORDER BY flag) AS flag FROM b) d1 JOIN a ON a.id=d1.id",
    "window-distinct CTE chain": "WITH ranked AS (SELECT id,ROW_NUMBER() OVER (PARTITION BY id ORDER BY flag) AS flag FROM b), filtered AS (SELECT DISTINCT id,flag FROM ranked) SELECT a.id,a.n,filtered.flag FROM filtered JOIN a ON a.id=filtered.id",
    "window-distinct CTE renamed": "WITH ranked(raw_id,raw_flag) AS (SELECT id,ROW_NUMBER() OVER (PARTITION BY id ORDER BY flag) FROM b), filtered(id,flag) AS (SELECT DISTINCT raw_id,raw_flag FROM ranked) SELECT a.id,a.n,filtered.flag FROM filtered JOIN a ON a.id=filtered.id",
    "window-distinct CTE star": "WITH ranked AS (SELECT id,ROW_NUMBER() OVER (PARTITION BY id ORDER BY flag) AS flag FROM b), filtered AS (SELECT DISTINCT * FROM ranked) SELECT a.id,a.n,filtered.flag FROM filtered JOIN a ON a.id=filtered.id",
    "window-distinct CTE repeated": "WITH ranked AS (SELECT id,ROW_NUMBER() OVER (PARTITION BY id ORDER BY flag) AS flag FROM b), filtered AS (SELECT DISTINCT id,flag FROM ranked) SELECT a.id,a.n,f.flag FROM filtered f JOIN filtered g ON g.id=f.id AND g.flag=f.flag JOIN a ON a.id=f.id",
    "window-union CTE chain": "WITH ranked AS (SELECT id,ROW_NUMBER() OVER (PARTITION BY id ORDER BY flag) AS flag FROM b), expanded AS (SELECT id,flag FROM ranked UNION ALL SELECT id,flag+10 FROM ranked) SELECT a.id,a.n,expanded.flag FROM expanded JOIN a ON a.id=expanded.id",
    "window-union CTE ordered limited": "WITH ranked AS (SELECT id,ROW_NUMBER() OVER (PARTITION BY id ORDER BY flag) AS flag FROM b), expanded AS (SELECT id,flag FROM ranked UNION ALL SELECT id,flag+10 FROM ranked ORDER BY flag DESC LIMIT 2) SELECT a.id,a.n,expanded.flag FROM expanded JOIN a ON a.id=expanded.id",
    "recursive-union CTE chain": "WITH RECURSIVE numbers(id,flag) AS (SELECT id,flag FROM b UNION ALL SELECT id,flag+1 FROM numbers WHERE flag<8), expanded AS (SELECT id,flag FROM numbers UNION ALL SELECT id,flag+10 FROM numbers) SELECT a.id,a.n,expanded.flag FROM expanded JOIN a ON a.id=expanded.id",
    "volatile CTE repeated": "WITH sampled AS (SELECT id,RAND() AS flag FROM b), filtered AS (SELECT id,flag FROM sampled) SELECT a.id,a.n,f.flag FROM filtered f JOIN filtered g ON g.id=f.id AND g.flag=f.flag JOIN a ON a.id=f.id",
    "union-all derived rows": "SELECT a.id,a.n,u.flag FROM (SELECT id,flag FROM b UNION ALL SELECT id,aux AS flag FROM c) u JOIN a ON a.id=u.id",
    "union-distinct derived rows": "SELECT a.id,a.n,u.flag FROM (SELECT id,flag FROM b UNION DISTINCT SELECT id,aux AS flag FROM c) u JOIN a ON a.id=u.id",
    "union-all CTE rows": "WITH u AS (SELECT id,flag FROM b UNION ALL SELECT id,aux AS flag FROM c) SELECT a.id,a.n,u.flag FROM u JOIN a ON a.id=u.id",
    "union-distinct CTE rows": "WITH u AS (SELECT id,flag FROM b UNION DISTINCT SELECT id,aux AS flag FROM c) SELECT a.id,a.n,u.flag FROM u JOIN a ON a.id=u.id",
    "union-distinct derived ordered limited": "SELECT a.id,a.n,u.flag FROM (SELECT id,flag FROM b UNION DISTINCT SELECT id,aux AS flag FROM c ORDER BY flag DESC LIMIT 2) u JOIN a ON a.id=u.id",
    "union-distinct CTE ordered limited": "WITH u AS (SELECT id,flag FROM b UNION DISTINCT SELECT id,aux AS flag FROM c ORDER BY flag DESC LIMIT 2) SELECT a.id,a.n,u.flag FROM u JOIN a ON a.id=u.id",
    "union-all CTE ordered": "WITH u AS (SELECT id,flag FROM b UNION ALL SELECT id,aux AS flag FROM c ORDER BY flag DESC) SELECT a.id,a.n,u.flag FROM u JOIN a ON a.id=u.id",
    "union-all CTE limited": "WITH u AS (SELECT id,flag FROM b UNION ALL SELECT id,aux AS flag FROM c LIMIT 3) SELECT a.id,a.n,u.flag FROM u JOIN a ON a.id=u.id",
    "union-all CTE ordered limited": "WITH u AS (SELECT id,flag FROM b UNION ALL SELECT id,aux AS flag FROM c ORDER BY flag DESC LIMIT 2) SELECT a.id,a.n,u.flag FROM u JOIN a ON a.id=u.id",
    "union-all CTE offset": "WITH u AS (SELECT id,flag FROM b UNION ALL SELECT id,aux AS flag FROM c ORDER BY flag DESC LIMIT 2 OFFSET 1) SELECT a.id,a.n,u.flag FROM u JOIN a ON a.id=u.id",
    "union-all CTE renamed ordered": "WITH u(id,flag) AS (SELECT id AS raw_id,flag AS raw_flag FROM b UNION ALL SELECT id,aux FROM c ORDER BY raw_flag DESC LIMIT 2) SELECT a.id,a.n,u.flag FROM u JOIN a ON a.id=u.id",
    "union-all CTE duplicate originals": "WITH u(id,flag) AS (SELECT id AS x,flag AS x FROM b UNION ALL SELECT id,aux FROM c LIMIT 3) SELECT a.id,a.n,u.flag FROM u JOIN a ON a.id=u.id",
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
    "dependent lateral UNION target": "SELECT a.id,a.n,u.flag FROM b src JOIN LATERAL (SELECT src.id AS id,src.flag AS flag UNION DISTINCT SELECT src.id,src.flag+1 UNION DISTINCT SELECT src.id,src.flag+1) u ON TRUE JOIN a ON a.id=u.id",
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
                connection = connect(socket)
                version = run(connection, "SELECT VERSION()")[0][0]
                assert version.startswith("8.4.11"), version
                for sql in [
                    "CREATE DATABASE probe",
                    "USE probe",
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
                    assert run(
                        connection,
                        "SELECT IS_UPDATABLE FROM information_schema.VIEWS WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='v'",
                    ) == (("YES",),), name
                    if name == "dependent lateral UNION target":
                        assert run(connection, "SELECT flag FROM v ORDER BY flag") == (
                            (7,),
                            (8,),
                            (8,),
                            (9,),
                        )
                    if name in (
                        "window-union CTE chain",
                        "window-union CTE ordered limited",
                        "recursive-union CTE chain",
                    ):
                        expected_flags = {
                            "window-union CTE chain": (1, 2, 11, 12),
                            "window-union CTE ordered limited": (11, 12),
                            "recursive-union CTE chain": (7, 8, 8, 17, 18, 18),
                        }[name]
                        assert run(connection, "SELECT flag FROM v ORDER BY flag") == tuple(
                            (flag,) for flag in expected_flags
                        ), name
                    assert run(connection, "UPDATE v SET n=n+1 WHERE id=1") == 1, name
                    assert run(connection, "SELECT id,n FROM a ORDER BY id") == (
                        (1, 1),
                        (2, 0),
                    ), name
                    assert_error(connection, "INSERT INTO v(id,n) VALUES(3,0)", 1471)
                    assert_error(connection, "DELETE FROM v WHERE id=1", 1395)
                    run(connection, "DROP VIEW v")

                    run(connection, "UPDATE a SET n=0")
                    run(
                        connection,
                        "CREATE VIEW vc AS "
                        + definition
                        + " WHERE a.n >= 0 WITH CASCADED CHECK OPTION",
                    )
                    assert run(
                        connection,
                        "SELECT IS_UPDATABLE FROM information_schema.VIEWS WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='vc'",
                    ) == (("YES",),), name
                    assert_error(connection, "UPDATE vc SET n=-1 WHERE id=1", 1369)
                    assert run(connection, "SELECT id,n FROM a ORDER BY id") == (
                        (1, 0),
                        (2, 0),
                    ), name
                    run(connection, "DROP VIEW vc")

                    if name in (
                        "duplicate derived rows",
                        "union-all derived rows",
                        "union-all CTE rows",
                        "union-distinct derived rows",
                        "union-distinct CTE rows",
                    ):
                        for first, second in [(7, 8), (8, 7)]:
                            run(connection, "DELETE FROM b")
                            run(
                                connection,
                                f"INSERT INTO b VALUES(1,{first}),(1,{second})",
                            )
                            source_alias = "u" if "union-" in name else "d1"
                            run(
                                connection,
                                "CREATE VIEW vcp AS "
                                + definition
                                + f" WHERE {source_alias}.flag > a.n WITH CASCADED CHECK OPTION",
                            )
                            assert_error(
                                connection, "UPDATE vcp SET n=7 WHERE id=1", 1369
                            )
                            assert run(
                                connection, "SELECT id,n FROM a ORDER BY id"
                            ) == ((1, 0), (2, 0))
                            run(connection, "DROP VIEW vcp")
                            if name == "duplicate derived rows":
                                run(
                                    connection,
                                    "CREATE VIEW vcj AS SELECT a.id,a.n,d1.flag FROM (SELECT id,flag FROM b LIMIT 100) d1 JOIN a ON a.id=d1.id AND d1.flag>a.n WITH CASCADED CHECK OPTION",
                                )
                                assert_error(
                                    connection, "UPDATE vcj SET n=7 WHERE id=1", 1369
                                )
                                assert run(
                                    connection, "SELECT id,n FROM a ORDER BY id"
                                ) == ((1, 0), (2, 0))
                                run(connection, "DROP VIEW vcj")

                for source in ("derived", "cte"):
                    run(connection, "UPDATE a SET n=0")
                    run(connection, "DELETE FROM b")
                    run(connection, "INSERT INTO b VALUES(1,7),(1,7),(1,8)")
                    union = "SELECT id,flag FROM b UNION DISTINCT SELECT id,aux AS flag FROM c"
                    definition = (
                        f"SELECT a.id,a.n,u.flag FROM ({union}) u JOIN a ON a.id=u.id"
                        if source == "derived"
                        else f"WITH u AS ({union}) SELECT a.id,a.n,u.flag FROM u JOIN a ON a.id=u.id"
                    )
                    run(
                        connection,
                        f"CREATE VIEW distinct_checked AS {definition} WHERE u.flag>a.n WITH CASCADED CHECK OPTION",
                    )
                    assert run(
                        connection, "SELECT flag FROM distinct_checked ORDER BY flag"
                    ) == ((7,), (8,), (9,)), source
                    assert (
                        run(connection, "UPDATE distinct_checked SET n=n+1 WHERE id=1")
                        == 1
                    ), source
                    assert_error(
                        connection, "UPDATE distinct_checked SET n=7 WHERE id=1", 1369
                    )
                    assert run(connection, "SELECT n FROM a WHERE id=1") == ((1,),), (
                        source
                    )
                    run(connection, "DROP VIEW distinct_checked")
                run(connection, "DELETE FROM b")
                run(connection, "INSERT INTO b VALUES(1,8),(1,7)")
                run(connection, "UPDATE a SET n=6")
                run(
                    connection,
                    "CREATE VIEW union_ordered_checked AS WITH u(id,flag) AS "
                    "(SELECT id AS raw_id,flag AS raw_flag FROM b UNION ALL "
                    "SELECT id,aux FROM c ORDER BY raw_flag DESC LIMIT 2) "
                    "SELECT a.id,a.n,u.flag FROM u JOIN a ON a.id=u.id "
                    "WHERE u.flag>a.n WITH CASCADED CHECK OPTION",
                )
                assert run(
                    connection,
                    "SELECT flag FROM union_ordered_checked ORDER BY flag DESC",
                ) == ((9,), (8,))
                assert (
                    run(connection, "UPDATE union_ordered_checked SET n=7 WHERE id=1")
                    == 1
                )
                assert_error(
                    connection, "UPDATE union_ordered_checked SET n=8 WHERE id=1", 1369
                )
                assert run(connection, "SELECT n FROM a WHERE id=1") == ((7,),)
                run(connection, "DROP VIEW union_ordered_checked")

                run(
                    connection,
                    "CREATE VIEW union_duplicate_ordered AS WITH u(id,flag) AS "
                    "(SELECT id AS x,flag AS x FROM b UNION ALL SELECT id,aux FROM c "
                    "ORDER BY 2 DESC LIMIT 2) SELECT a.id,a.n,u.flag "
                    "FROM u JOIN a ON a.id=u.id",
                )
                assert run(
                    connection,
                    "SELECT is_updatable FROM information_schema.views WHERE table_name='union_duplicate_ordered'",
                ) == (("YES",),)
                assert_error(
                    connection,
                    "UPDATE union_duplicate_ordered SET n=n+1 WHERE id=1",
                    1052,
                )
                assert_error(
                    connection,
                    "UPDATE union_duplicate_ordered v JOIN a a2 ON a2.id=v.id SET v.n=v.n+1 WHERE v.id=1",
                    1052,
                )
                assert run(connection, "SELECT n FROM a WHERE id=1") == ((7,),)
                run(connection, "DROP VIEW union_duplicate_ordered")

                for order in ("1", "1+0"):
                    run(
                        connection,
                        "CREATE VIEW union_duplicate_probe AS WITH u(id,flag) AS "
                        "(SELECT id AS x,flag AS x FROM b UNION ALL SELECT id,aux FROM c "
                        f"ORDER BY {order} DESC LIMIT 2) SELECT a.id,a.n,u.flag "
                        "FROM u JOIN a ON a.id=u.id",
                    )
                    assert run(
                        connection,
                        "SELECT is_updatable FROM information_schema.views WHERE table_name='union_duplicate_probe'",
                    ) == (("YES",),)
                    if order == "1":
                        assert_error(
                            connection,
                            "UPDATE union_duplicate_probe SET n=n+1 WHERE id=1",
                            1052,
                        )
                    else:
                        assert (
                            run(
                                connection,
                                "UPDATE union_duplicate_probe SET n=n+1 WHERE id=1",
                            )
                            == 1
                        )
                    run(connection, "DROP VIEW union_duplicate_probe")
                assert run(connection, "SELECT n FROM a WHERE id=1") == ((8,),)
                run(connection, "UPDATE a SET n=7 WHERE id=1")

                for order in ("1", "2", "3"):
                    run(
                        connection,
                        "CREATE VIEW union_three_output_probe AS WITH u(id,flag,priority) AS "
                        "(SELECT id AS x,flag AS x,100 AS y FROM b UNION ALL "
                        f"SELECT id,aux,200 FROM c ORDER BY {order} DESC LIMIT 2) "
                        "SELECT a.id,a.n,u.flag FROM u JOIN a ON a.id=u.id",
                    )
                    assert run(
                        connection,
                        "SELECT is_updatable FROM information_schema.views WHERE table_name='union_three_output_probe'",
                    ) == (("YES",),)
                    if order == "3":
                        assert (
                            run(
                                connection,
                                "UPDATE union_three_output_probe SET n=n+1 WHERE id=1",
                            )
                            == 1
                        )
                    else:
                        assert_error(
                            connection,
                            "UPDATE union_three_output_probe SET n=n+1 WHERE id=1",
                            1052,
                        )
                    run(connection, "DROP VIEW union_three_output_probe")
                assert run(connection, "SELECT n FROM a WHERE id=1") == ((8,),)
                run(connection, "UPDATE a SET n=7 WHERE id=1")

                run(connection, "CREATE TABLE insert_target(id INT PRIMARY KEY,n INT)")
                run(
                    connection,
                    "CREATE VIEW insert_checked AS SELECT z.id,z.n,b.flag FROM insert_target z JOIN b ON b.id=z.id WHERE b.flag>z.n WITH CASCADED CHECK OPTION",
                )
                assert_error(
                    connection, "INSERT INTO insert_checked(id,n) VALUES(1,7)", 1369
                )
                assert run(connection, "SELECT id,n FROM insert_target") == ()
                assert (
                    run(connection, "INSERT INTO insert_checked(id,n) VALUES(1,6)") == 1
                )
                assert run(connection, "SELECT id,n FROM insert_target") == ((1, 6),)
                run(connection, "INSERT INTO b VALUES(2,7),(2,8)")
                run(
                    connection,
                    "CREATE VIEW insert_on_checked AS SELECT z.id,z.n,b.flag FROM insert_target z JOIN b ON b.id=z.id AND b.flag>z.n WITH CASCADED CHECK OPTION",
                )
                assert (
                    run(connection, "INSERT INTO insert_on_checked(id,n) VALUES(2,7)")
                    == 1
                )
                assert run(
                    connection, "SELECT id,n FROM insert_target ORDER BY id"
                ) == ((1, 6), (2, 7))

                run(
                    connection,
                    "CREATE TABLE insert_order_target(id INT PRIMARY KEY,n INT)",
                )
                run(connection, "CREATE TABLE insert_order_source(id INT,flag INT)")
                run(
                    connection,
                    "CREATE VIEW insert_order_checked AS SELECT t.id,t.n,s.flag FROM insert_order_target t JOIN insert_order_source s ON s.id=t.id WHERE s.flag>t.n WITH CASCADED CHECK OPTION",
                )
                for first, second, accepted in ((7, 8, True), (8, 7, False)):
                    run(connection, "DELETE FROM insert_order_target")
                    run(connection, "DELETE FROM insert_order_source")
                    run(
                        connection,
                        f"INSERT INTO insert_order_source VALUES(1,{first}),(1,{second})",
                    )
                    if accepted:
                        assert (
                            run(
                                connection,
                                "INSERT INTO insert_order_checked(id,n) VALUES(1,7)",
                            )
                            == 1
                        )
                        assert run(
                            connection, "SELECT id,n FROM insert_order_target"
                        ) == ((1, 7),)
                    else:
                        assert_error(
                            connection,
                            "INSERT INTO insert_order_checked(id,n) VALUES(1,7)",
                            1369,
                        )
                        assert (
                            run(connection, "SELECT id,n FROM insert_order_target")
                            == ()
                        )

                for index_layout in ("heap", "id_flag"):
                    if index_layout == "id_flag":
                        run(
                            connection,
                            "CREATE INDEX ix_insert_order_id_flag ON insert_order_source(id,flag)",
                        )
                        run(
                            connection,
                            "CREATE VIEW insert_order_forced AS SELECT t.id,t.n,s.flag FROM insert_order_target t JOIN insert_order_source s FORCE INDEX(ix_insert_order_id_flag) ON s.id=t.id WHERE s.flag>t.n WITH CASCADED CHECK OPTION",
                        )
                    for flags in (
                        (6, 8),
                        (8, 6),
                        (7, 7, 8),
                        (8, 7, 7),
                        (7, 8, 7),
                        (8, 8, 7),
                        (8, 7, 8),
                    ):
                        run(connection, "DELETE FROM insert_order_target")
                        run(connection, "DELETE FROM insert_order_source")
                        values = ",".join(f"(1,{flag})" for flag in flags)
                        run(
                            connection,
                            f"INSERT INTO insert_order_source VALUES{values}",
                        )
                        view = (
                            "insert_order_forced"
                            if index_layout == "id_flag"
                            else "insert_order_checked"
                        )
                        try:
                            run(connection, f"INSERT INTO {view}(id,n) VALUES(1,7)")
                            outcome = "accepted"
                        except pymysql.MySQLError as error:
                            assert error.args[0] == 1369, error
                            outcome = "rejected"
                        assert outcome == (
                            "accepted" if flags[-1] == 8 else "rejected"
                        ), (index_layout, flags, outcome)
                        print(
                            "joined-view insert source order",
                            index_layout,
                            flags,
                            outcome,
                        )

                for read_flag in (7, 8):
                    run(connection, "DELETE FROM insert_order_target")
                    run(connection, "DELETE FROM insert_order_source")
                    run(connection, "INSERT INTO insert_order_source VALUES(1,7),(1,8)")
                    assert run(
                        connection,
                        f"SELECT flag FROM insert_order_source WHERE flag={read_flag}",
                    ) == ((read_flag,),)
                    try:
                        run(
                            connection,
                            "INSERT INTO insert_order_checked(id,n) VALUES(1,7)",
                        )
                        outcome = "accepted"
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1369, error
                        outcome = "rejected"
                    assert outcome == "accepted", (read_flag, outcome)
                    print("joined-view insert after source read", read_flag, outcome)

                for updated_flag in (7, 8):
                    run(connection, "DELETE FROM insert_order_target")
                    run(connection, "DELETE FROM insert_order_source")
                    run(connection, "INSERT INTO insert_order_source VALUES(1,7),(1,8)")
                    run(
                        connection,
                        f"UPDATE insert_order_source SET flag=flag WHERE flag={updated_flag}",
                    )
                    try:
                        run(
                            connection,
                            "INSERT INTO insert_order_checked(id,n) VALUES(1,7)",
                        )
                        outcome = "accepted"
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1369, error
                        outcome = "rejected"
                    assert outcome == "accepted", (updated_flag, outcome)
                    print(
                        "joined-view insert after source update", updated_flag, outcome
                    )

                run(connection, "DELETE FROM insert_order_target")
                run(connection, "DELETE FROM insert_order_source")
                run(connection, "INSERT INTO insert_order_source VALUES(1,7),(1,8)")
                before_reads = dict(
                    run(connection, "SHOW SESSION STATUS LIKE 'Handler_read%'")
                )
                run(connection, "INSERT INTO insert_order_checked(id,n) VALUES(1,7)")
                after_reads = dict(
                    run(connection, "SHOW SESSION STATUS LIKE 'Handler_read%'")
                )
                read_deltas = {
                    name: int(after_reads[name]) - int(before_reads[name])
                    for name in before_reads
                    if int(after_reads[name]) != int(before_reads[name])
                }
                assert read_deltas == {}, read_deltas
                print("joined-view insert handler reads", read_deltas)

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
                assert run(
                    connection,
                    "SELECT IS_UPDATABLE FROM information_schema.VIEWS WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='alias_outer'",
                ) == (("YES",),)
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
                assert run(
                    connection,
                    "SELECT IS_UPDATABLE FROM information_schema.VIEWS WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='two_source_outer'",
                ) == (("YES",),)
                assert (
                    run(connection, "UPDATE two_source_outer SET n=11 WHERE id=1") == 1
                )
                assert_error(
                    connection, "UPDATE two_source_outer SET n=0 WHERE id=1", 1369
                )
                assert run(connection, "SELECT n FROM two_source_target") == ((11,),)

                for sql in [
                    (
                        "CREATE VIEW lateral_checked_inner AS SELECT a.id AS target_id,a.n AS metric,d.flag,e.x "
                        "FROM (SELECT id,SUM(flag) flag FROM b GROUP BY id) d "
                        "JOIN LATERAL (SELECT d.id x) e ON TRUE JOIN a ON a.id=e.x "
                        "WHERE a.n>=0 WITH CASCADED CHECK OPTION"
                    ),
                    (
                        "CREATE VIEW lateral_checked_outer AS SELECT iv.target_id AS id,iv.metric AS n,s.aux "
                        "FROM lateral_checked_inner iv JOIN (SELECT id,SUM(aux) aux FROM c GROUP BY id) s "
                        "ON s.id=iv.target_id WHERE iv.metric<s.aux WITH CASCADED CHECK OPTION"
                    ),
                ]:
                    run(connection, sql)
                assert run(
                    connection,
                    "SELECT IS_UPDATABLE FROM information_schema.VIEWS WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='lateral_checked_outer'",
                ) == (("YES",),)
                run(connection, "UPDATE a SET n=0")
                assert (
                    run(connection, "UPDATE lateral_checked_outer SET n=1 WHERE id=1")
                    == 1
                )
                assert_error(
                    connection, "UPDATE lateral_checked_outer SET n=-1 WHERE id=1", 1369
                )
                assert_error(
                    connection, "UPDATE lateral_checked_outer SET n=10 WHERE id=1", 1369
                )
                assert run(connection, "SELECT id,n FROM a ORDER BY id") == (
                    (1, 1),
                    (2, 0),
                )

                print(
                    f"MySQL {version}: {len(SHAPES)} composed join-view shapes, private aliases, and two leading sources matched the expected write contract"
                )
            finally:
                if connection:
                    connection.close()
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
