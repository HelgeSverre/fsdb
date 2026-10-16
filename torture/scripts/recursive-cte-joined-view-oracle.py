"""Check writable joins beside recursive CTE sources against native MySQL 8.4."""

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


def main():
    with tempfile.TemporaryDirectory(prefix="fsdb-recursive-view-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
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
                    for query in [
                        "CREATE DATABASE probe", "USE probe",
                        "CREATE TABLE target(id INT PRIMARY KEY,n INT)",
                        "INSERT INTO target VALUES(1,10),(2,20),(3,30),(4,40)",
                    ]:
                        cursor.execute(query)

                    recursive = "WITH RECURSIVE seq(k) AS (SELECT 1 UNION ALL SELECT k+1 FROM seq WHERE k<3)"
                    cases = [
                        ("recursive_first", f"{recursive} SELECT t.id,t.n FROM seq JOIN target t ON t.id=seq.k"),
                        ("recursive_last", f"{recursive} SELECT t.id,t.n FROM target t JOIN seq ON t.id=seq.k"),
                        ("recursive_group", f"{recursive} SELECT t.id,t.n FROM (seq JOIN target t ON t.id=seq.k)"),
                        ("recursive_inferred",
                         "WITH RECURSIVE seq AS (SELECT 1 AS k UNION ALL SELECT k+1 FROM seq WHERE k<3) "
                         "SELECT t.id,t.n FROM seq JOIN target t ON t.id=seq.k"),
                        ("recursive_dependent",
                         f"{recursive}, doubled AS (SELECT k,k*2 AS v FROM seq) "
                         "SELECT t.id,t.n FROM doubled d JOIN target t ON t.id=d.k"),
                        ("recursive_grouped",
                         f"{recursive}, grouped AS (SELECT k,COUNT(*) AS c FROM seq GROUP BY k) "
                         "SELECT t.id,t.n FROM grouped g JOIN target t ON t.id=g.k"),
                        ("recursive_repeated",
                         f"{recursive} SELECT t.id,t.n FROM seq a JOIN seq b ON b.k=a.k "
                         "JOIN target t ON t.id=a.k"),
                        ("recursive_star",
                         f"{recursive}, copied AS (SELECT * FROM seq) "
                         "SELECT t.id,t.n FROM copied c JOIN target t ON t.id=c.k"),
                        ("recursive_nested_star",
                         f"{recursive}, copied AS (SELECT k FROM seq), again AS (SELECT * FROM copied) "
                         "SELECT t.id,t.n FROM again a JOIN target t ON t.id=a.k"),
                        ("recursive_nested_derived",
                         f"{recursive}, copied AS (SELECT d.k FROM (SELECT k FROM seq) d) "
                         "SELECT t.id,t.n FROM copied c JOIN target t ON t.id=c.k"),
                        ("recursive_nested_join",
                         f"{recursive}, copied AS (SELECT s.k FROM seq s JOIN "
                         "(SELECT 2 AS anchor) d ON d.anchor=s.k) "
                         "SELECT t.id,t.n FROM copied c JOIN target t ON t.id=c.k"),
                        ("recursive_lateral_after_seq",
                         f"{recursive} SELECT t.id,t.n FROM seq s JOIN LATERAL "
                         "(SELECT s.k AS lookup_key) l ON TRUE JOIN target t ON t.id=l.lookup_key"),
                        ("recursive_lateral_nested_after_seq",
                         f"{recursive} SELECT t.id,t.n FROM seq s JOIN LATERAL "
                         "(SELECT s.k AS lookup_key FROM (SELECT 1 AS marker) d) l ON TRUE "
                         "JOIN target t ON t.id=l.lookup_key"),
                        ("recursive_lateral_cte_reread",
                         f"{recursive} SELECT t.id,t.n FROM seq s JOIN LATERAL "
                         "(SELECT k AS lookup_key FROM seq WHERE k=s.k) l ON TRUE "
                         "JOIN target t ON t.id=l.lookup_key"),
                        ("recursive_lateral_volatile_reread",
                         "WITH RECURSIVE seq(k,r) AS (SELECT 1,RAND() UNION ALL "
                         "SELECT k+1,r FROM seq WHERE k<3) "
                         "SELECT t.id,t.n FROM seq s JOIN LATERAL "
                         "(SELECT k AS lookup_key FROM seq WHERE k=s.k AND r=s.r) l ON TRUE "
                         "JOIN target t ON t.id=l.lookup_key"),
                        ("recursive_lateral_nested_cte_reread",
                         f"{recursive} SELECT t.id,t.n FROM seq s JOIN LATERAL "
                         "(SELECT d.k AS lookup_key FROM (SELECT k FROM seq) d WHERE d.k=s.k) l ON TRUE "
                         "JOIN target t ON t.id=l.lookup_key"),
                        ("recursive_lateral_nested_volatile_reread",
                         "WITH RECURSIVE seq(k,r) AS (SELECT 1,RAND() UNION ALL "
                         "SELECT k+1,r FROM seq WHERE k<3) "
                         "SELECT t.id,t.n FROM seq s JOIN LATERAL "
                         "(SELECT d.k AS lookup_key FROM (SELECT k,r FROM seq) d "
                         "WHERE d.k=s.k AND d.r=s.r) l ON TRUE "
                         "JOIN target t ON t.id=l.lookup_key"),
                        ("recursive_lateral_independent_scalar",
                         f"{recursive} SELECT t.id,t.n FROM seq s JOIN LATERAL "
                         "(SELECT (SELECT s.k) AS lookup_key) l ON TRUE "
                         "JOIN target t ON t.id=l.lookup_key"),
                        ("recursive_lateral_independent_exists",
                         f"{recursive} SELECT t.id,t.n FROM seq s JOIN LATERAL "
                         "(SELECT s.k AS lookup_key WHERE EXISTS (SELECT 1 WHERE s.k>0)) l ON TRUE "
                         "JOIN target t ON t.id=l.lookup_key"),
                        ("recursive_json_after_seq",
                         f"{recursive} SELECT t.id,t.n FROM seq s JOIN "
                         "JSON_TABLE(JSON_ARRAY(s.k),'$[*]' COLUMNS(k INT PATH '$')) j ON j.k=s.k "
                         "JOIN target t ON t.id=j.k"),
                        ("recursive_shared_volatile",
                         "WITH RECURSIVE seq(k,r) AS (SELECT 1,RAND() UNION ALL "
                         "SELECT k+1,r FROM seq WHERE k<3) "
                         "SELECT t.id,t.n FROM seq a JOIN seq b ON b.k=a.k AND b.r=a.r "
                         "JOIN target t ON t.id=a.k"),
                    ]
                    for index, (name, body) in enumerate(cases, start=1):
                        cursor.execute(f"CREATE VIEW {name} AS {body}")
                        cursor.execute(
                            "SELECT IS_UPDATABLE FROM information_schema.views "
                            "WHERE table_schema='probe' AND table_name=%s", (name,),
                        )
                        assert cursor.fetchone() == ("YES",), name
                        cursor.execute(f"UPDATE {name} SET n=n+1 WHERE id=2")
                        assert cursor.rowcount == 1, name
                        cursor.execute("SELECT n FROM target WHERE id=2")
                        assert cursor.fetchone() == (20 + index,), name

                    cursor.execute(
                        f"CREATE VIEW recursive_checked AS {recursive} "
                        "SELECT t.id,t.n FROM seq JOIN target t ON t.id=seq.k "
                        "WHERE t.n>=0 WITH CASCADED CHECK OPTION"
                    )
                    try:
                        cursor.execute("UPDATE recursive_checked SET n=-1 WHERE id=2")
                        raise AssertionError("CHECK OPTION allowed an invalid recursive-view write")
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1369, error.args

                    cursor.execute(f"CREATE VIEW recursive_only AS {recursive} SELECT k FROM seq")
                    cursor.execute(
                        "SELECT IS_UPDATABLE FROM information_schema.views "
                        "WHERE table_schema='probe' AND table_name='recursive_only'"
                    )
                    assert cursor.fetchone() == ("YES",)  # MySQL metadata overstates this view.
                    try:
                        cursor.execute("UPDATE recursive_only SET k=4")
                        raise AssertionError("recursive-only view accepted UPDATE")
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1288, error.args

                    waiter = connect(socket)
                    waiter.select_db("probe")
                    try:
                        cursor.execute("UPDATE target SET n=0 WHERE id=2")
                        cursor.execute("BEGIN")
                        cursor.execute("UPDATE target SET n=n+1 WHERE id=2")
                        started = threading.Event()
                        finished = threading.Event()
                        outcome = {}

                        def waiting_update():
                            try:
                                with waiter.cursor() as waiting_cursor:
                                    waiting_cursor.execute("BEGIN")
                                    started.set()
                                    waiting_cursor.execute(
                                        "UPDATE recursive_dependent SET n=n+1 WHERE id=2"
                                    )
                                    outcome["affected"] = waiting_cursor.rowcount
                                    waiting_cursor.execute("COMMIT")
                            except Exception as error:
                                outcome["error"] = error
                            finally:
                                finished.set()

                        thread = threading.Thread(target=waiting_update, daemon=True)
                        thread.start()
                        assert started.wait(2), "recursive view writer did not start"
                        assert not finished.wait(0.2), "recursive view writer did not wait"
                        cursor.execute("COMMIT")
                        assert finished.wait(5), "recursive view writer did not resume"
                        thread.join()
                        assert "error" not in outcome, outcome
                        assert outcome["affected"] == 1, outcome
                        cursor.execute("SELECT n FROM target WHERE id=2")
                        assert cursor.fetchone() == (2,)
                    finally:
                        waiter.close()

                    print(f"MySQL {version}: {len(cases)} recursive-source writes, two rejections, and a rebase match")
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
