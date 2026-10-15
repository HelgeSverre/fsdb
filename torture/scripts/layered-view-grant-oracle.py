"""Probe grant errors for direct and JSON_TABLE layered views on MySQL 8.4.11."""

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


def run(connection, sql):
    with connection.cursor() as cursor:
        cursor.execute(sql)
        return cursor.fetchall() if cursor.description else cursor.rowcount


def assert_error(connection, sql, code):
    try:
        run(connection, sql)
    except pymysql.MySQLError as error:
        assert error.args[0] == code, (sql, error)
        return error.args
    else:
        raise AssertionError(f"Expected MySQL error {code}: {sql}")


def main():
    with tempfile.TemporaryDirectory(prefix="fsdb-layered-view-grants-") as directory:
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
                    "CREATE TABLE target(id INT PRIMARY KEY,n INT)",
                    "INSERT INTO target VALUES(1,0)",
                    "CREATE USER inner_owner", "CREATE USER outer_owner",
                    "GRANT SELECT,UPDATE ON probe.target TO inner_owner",
                    "GRANT CREATE VIEW ON probe.* TO inner_owner,outer_owner",
                    "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW inner_view AS SELECT id,n FROM target",
                    "GRANT SELECT,UPDATE ON probe.inner_view TO outer_owner",
                    "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW outer_view AS SELECT id,n AS metric FROM inner_view",
                    "CREATE USER nested_writer",
                    "GRANT SELECT,UPDATE ON probe.outer_view TO nested_writer",
                ]:
                    run(connection, sql)

                update = "UPDATE outer_view SET metric=metric+1 WHERE id=1"
                assert run(connection, update) == 1
                run(connection, "REVOKE UPDATE ON probe.inner_view FROM outer_owner")
                assert_error(connection, update, 1356)
                run(connection, "GRANT UPDATE(n) ON probe.inner_view TO outer_owner")
                assert run(connection, update) == 1
                run(connection, "REVOKE SELECT ON probe.target FROM inner_owner")
                assert_error(connection, update, 1356)
                run(connection, "GRANT SELECT ON probe.target TO inner_owner")
                run(connection, "REVOKE UPDATE ON probe.target FROM inner_owner")
                assert_error(connection, update, 1356)
                writer = pymysql.connect(unix_socket=socket, user="nested_writer", autocommit=True, database="probe")
                try:
                    assert_error(writer, update, 1356)
                finally:
                    writer.close()
                assert run(connection, "SELECT n FROM target") == ((2,),)

                run(connection, "GRANT UPDATE ON probe.target TO inner_owner")
                for sql in [
                    "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW json_inner AS SELECT a.id,a.n,j.x FROM target a JOIN JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j ON j.x=a.id",
                    "GRANT SELECT,UPDATE ON probe.json_inner TO outer_owner",
                    "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW json_outer AS SELECT id,n AS metric,x FROM json_inner",
                ]:
                    run(connection, sql)
                assert run(connection, "UPDATE json_outer SET metric=metric+1 WHERE id=1") == 1
                run(connection, "REVOKE SELECT ON probe.target FROM inner_owner")
                denied = assert_error(connection, "UPDATE json_outer SET metric=metric+1 WHERE id=1", 1143)
                assert "column 'id'" in denied[1], denied
                run(connection, "GRANT SELECT(id) ON probe.target TO inner_owner")
                assert_error(connection, "UPDATE json_outer SET metric=metric WHERE id=1", 1356)
                run(connection, "GRANT SELECT(n) ON probe.target TO inner_owner")
                assert run(connection, "UPDATE json_outer SET metric=metric WHERE id=1") == 0
                run(connection, "REVOKE SELECT(id,n) ON probe.target FROM inner_owner")
                assert run(connection, "SELECT n FROM target") == ((3,),)
                run(connection, "GRANT SELECT ON probe.target TO inner_owner")
                for sql in [
                    "CREATE TABLE companion(id INT PRIMARY KEY)",
                    "INSERT INTO companion VALUES(1)",
                    "GRANT SELECT ON probe.companion TO inner_owner",
                    "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW joined_inner AS SELECT a.id,a.n FROM target a JOIN companion b ON b.id=a.id",
                    "GRANT SELECT,UPDATE ON probe.joined_inner TO outer_owner",
                    "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW joined_outer AS SELECT id,n AS metric FROM joined_inner",
                ]:
                    run(connection, sql)
                assert run(connection, "UPDATE joined_outer SET metric=metric WHERE id=1") == 0
                run(connection, "REVOKE SELECT ON probe.target FROM inner_owner")
                denied = assert_error(connection, "UPDATE joined_outer SET metric=metric WHERE id=1", 1143)
                assert "column 'id'" in denied[1], denied
                run(connection, "GRANT SELECT ON probe.target TO inner_owner")
                for sql in [
                    "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW dependent_inner AS SELECT a.id,a.n,j.x FROM target a JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=a.id",
                    "GRANT SELECT,UPDATE ON probe.dependent_inner TO outer_owner",
                    "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW dependent_outer AS SELECT id,n AS metric,x FROM dependent_inner",
                ]:
                    run(connection, sql)
                assert run(connection, "UPDATE dependent_outer SET metric=metric+1 WHERE id=1") == 1
                run(connection, "REVOKE SELECT ON probe.target FROM inner_owner")
                assert_error(connection, "UPDATE dependent_outer SET metric=metric+1 WHERE id=1", 1143)
                assert run(connection, "SELECT n FROM target") == ((4,),)
                run(connection, "GRANT SELECT ON probe.target TO inner_owner")
                inner = pymysql.connect(unix_socket=socket, user="inner_owner", autocommit=True, database="probe")
                try:
                    run(inner, "CREATE VIEW inner_secured AS SELECT id,n FROM target")
                finally:
                    inner.close()
                run(connection, "GRANT SELECT,UPDATE ON probe.inner_secured TO outer_owner")
                outer = pymysql.connect(unix_socket=socket, user="outer_owner", autocommit=True, database="probe")
                try:
                    run(outer, "CREATE VIEW outer_secured AS SELECT id,n FROM inner_secured")
                finally:
                    outer.close()
                run(connection, "GRANT SELECT,UPDATE ON probe.outer_secured TO nested_writer")
                run(connection, "REVOKE UPDATE ON probe.target FROM inner_owner")
                writer = pymysql.connect(unix_socket=socket, user="nested_writer", autocommit=True, database="probe")
                try:
                    assert_error(writer, "UPDATE outer_secured SET n=n+1 WHERE id=1", 1356)
                finally:
                    writer.close()
                print(f"MySQL {version}: direct layered grants 1356; joined-view key denial 1143, projection denial 1356")
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
