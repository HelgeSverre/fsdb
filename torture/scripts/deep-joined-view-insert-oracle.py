"""Probe multi-layer definer grants for joined-view INSERT on MySQL 8.4.11."""

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
    try:
        with connection.cursor() as cursor:
            cursor.execute(sql)
            return cursor.fetchall() if sql.startswith("SELECT") else cursor.rowcount
    except pymysql.MySQLError as error:
        return ("error", error.args[0])


def main():
    with tempfile.TemporaryDirectory(prefix="fsdb-deep-view-oracle-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql-data"
        data.mkdir()
        socket = str(root / "mysql.sock")
        server = connection = None
        with (root / "mysql.log").open("w") as log:
            try:
                subprocess.run(["mysqld", "--no-defaults", "--initialize-insecure",
                                f"--datadir={data}", "--innodb-redo-log-capacity=64M"],
                               check=True, stdout=log, stderr=subprocess.STDOUT)
                server = subprocess.Popen(
                    ["mysqld", "--no-defaults", f"--datadir={data}",
                     "--skip-networking", f"--socket={socket}",
                     f"--pid-file={root / 'mysql.pid'}", "--mysqlx=0",
                     "--innodb-buffer-pool-size=64M", "--innodb-redo-log-capacity=64M"],
                    stdout=log, stderr=subprocess.STDOUT)
                connection = connect(socket)
                version = run(connection, "SELECT VERSION()")[0][0]
                assert version.startswith("8.4.11"), version
                setup = [
                    "CREATE DATABASE probe", "USE probe",
                    "CREATE TABLE a(id INT PRIMARY KEY AUTO_INCREMENT,n INT)",
                    "CREATE TABLE b(id INT PRIMARY KEY,left_id INT)",
                    "INSERT INTO b VALUES(1,1)",
                    "CREATE USER inner_owner", "CREATE USER middle_owner", "CREATE USER outer_owner",
                    "GRANT SELECT,INSERT,UPDATE ON probe.a TO inner_owner",
                    "GRANT SELECT,INSERT,UPDATE ON probe.b TO outer_owner",
                    "GRANT CREATE VIEW ON probe.* TO inner_owner,middle_owner,outer_owner",
                    "CREATE DEFINER='inner_owner'@'%' VIEW inner_v AS SELECT id,n AS value FROM a",
                    "GRANT SELECT,INSERT,UPDATE ON probe.inner_v TO middle_owner",
                    "CREATE DEFINER='middle_owner'@'%' VIEW middle_v AS SELECT id,value AS metric FROM inner_v",
                    "GRANT SELECT,INSERT,UPDATE ON probe.middle_v TO outer_owner",
                    "CREATE DEFINER='outer_owner'@'%' VIEW outer_v AS SELECT m.id,m.metric,b.id AS b_id FROM middle_v m JOIN b ON b.left_id=m.id",
                ]
                for sql in setup:
                    result = run(connection, sql)
                    assert not isinstance(result, tuple), (sql, result)
                cases = [
                    "INSERT INTO outer_v(metric) VALUES(10)",
                    "INSERT INTO outer_v(id,metric) VALUES(2,20)",
                    "REVOKE INSERT ON probe.inner_v FROM middle_owner",
                    "INSERT INTO outer_v(metric) VALUES(30)",
                    "GRANT INSERT(value) ON probe.inner_v TO middle_owner",
                    "INSERT INTO outer_v(metric) VALUES(40)",
                    "INSERT INTO outer_v(metric) SELECT 45",
                    "INSERT INTO outer_v(id,metric) VALUES(5,50)",
                    "GRANT INSERT ON probe.inner_v TO middle_owner",
                    "REVOKE UPDATE ON probe.inner_v FROM middle_owner",
                    "INSERT INTO outer_v(id,metric) VALUES(2,25) ON DUPLICATE KEY UPDATE id=id+10",
                    "GRANT UPDATE(value) ON probe.inner_v TO middle_owner",
                    "INSERT INTO outer_v(id,metric) VALUES(2,25) ON DUPLICATE KEY UPDATE metric=VALUES(metric)",
                    "INSERT INTO outer_v(id,metric) VALUES(2,30) ON DUPLICATE KEY UPDATE id=id+10",
                    "INSERT INTO outer_v(id,metric) VALUES(5,50) ON DUPLICATE KEY UPDATE id=id+10",
                    "REVOKE UPDATE ON probe.middle_v FROM outer_owner",
                    "GRANT UPDATE(metric) ON probe.middle_v TO outer_owner",
                    "INSERT INTO outer_v(id,metric) VALUES(5,55) ON DUPLICATE KEY UPDATE id=id+10",
                    "SELECT id,n FROM a ORDER BY id",
                ]
                for sql in cases:
                    print(sql, "=>", run(connection, sql))
            except Exception:
                print((root / "mysql.log").read_text())
                raise
            finally:
                if connection:
                    connection.close()
                if server and server.poll() is None:
                    subprocess.run(["mysqladmin", "--no-defaults", f"--socket={socket}",
                                    "-uroot", "shutdown"], stdout=subprocess.DEVNULL,
                                   stderr=subprocess.DEVNULL)
                    server.wait(timeout=20)


if __name__ == "__main__":
    main()
