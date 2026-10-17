"""Compare layered writable joins with derived read sources on MySQL 8.4.11."""

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


def run(connection, sql):
    with connection.cursor() as cursor:
        cursor.execute(sql)
        return cursor.fetchall() if cursor.description else cursor.rowcount


def outcome(connection, sql):
    try:
        return "ok", run(connection, sql)
    except pymysql.MySQLError as error:
        return "error", error.args[0]


def exercise(connection):
    for sql in [
        "CREATE DATABASE probe", "USE probe",
        "CREATE TABLE target(id INT PRIMARY KEY,n INT)",
        "CREATE TABLE flags(id INT PRIMARY KEY,flag INT)",
        "CREATE TABLE aux(id INT PRIMARY KEY,aux INT)",
        "CREATE TABLE aux_shadow(id INT PRIMARY KEY,aux INT,n INT)",
        "INSERT INTO target VALUES(1,0),(2,0)",
        "INSERT INTO flags VALUES(1,7)",
        "INSERT INTO aux VALUES(1,9)",
        "INSERT INTO aux_shadow VALUES(1,9,-1)",
        "CREATE USER inner_owner", "CREATE USER outer_owner",
        "GRANT SELECT,UPDATE ON probe.target TO inner_owner",
        "GRANT SELECT ON probe.flags TO inner_owner",
        "GRANT SELECT ON probe.aux TO inner_owner",
        "GRANT SELECT ON probe.aux_shadow TO inner_owner",
        "GRANT CREATE VIEW ON probe.* TO inner_owner,outer_owner",
        "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW inner_join AS "
        "SELECT t.id,t.n,d1.flag,d2.aux "
        "FROM (SELECT id,SUM(flag) flag FROM flags GROUP BY id) d1 "
        "JOIN (SELECT id,SUM(aux) aux FROM aux GROUP BY id) d2 ON d2.id=d1.id "
        "JOIN target t ON t.id=d1.id AND t.n<d2.aux",
        "GRANT SELECT,UPDATE ON probe.inner_join TO outer_owner",
        "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW outer_join "
        "AS SELECT id,n AS metric FROM inner_join",
    ]:
        run(connection, sql)

    update = "UPDATE outer_join SET metric=metric+1 WHERE id=1"
    results = [
        ("metadata", outcome(connection, "SELECT IS_UPDATABLE FROM information_schema.VIEWS "
         "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='outer_join'")),
        ("initial", outcome(connection, update)),
    ]
    for table in ("flags", "aux", "target"):
        run(connection, f"REVOKE SELECT ON probe.{table} FROM inner_owner")
        results.append((f"revoked {table}", outcome(connection, update)))
        run(connection, f"GRANT SELECT ON probe.{table} TO inner_owner")
    run(connection, "REVOKE SELECT ON probe.flags FROM inner_owner")
    run(connection, "REVOKE SELECT ON probe.target FROM inner_owner")
    results.append(("revoked flags and target", outcome(connection, update)))
    run(connection, "GRANT SELECT ON probe.flags TO inner_owner")
    run(connection, "GRANT SELECT ON probe.target TO inner_owner")
    for sql in [
        "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW grouped_inner AS "
        "SELECT t.id,t.n,d.flag FROM target t JOIN "
        "((SELECT id,SUM(flag) flag FROM flags GROUP BY id) d "
        "JOIN aux a ON a.id=d.id) ON t.id=d.id",
        "GRANT SELECT,UPDATE ON probe.grouped_inner TO outer_owner",
        "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW grouped_outer "
        "AS SELECT id,n AS metric FROM grouped_inner",
    ]:
        run(connection, sql)
    grouped_update = "UPDATE grouped_outer SET metric=metric+1 WHERE id=1"
    results.append(("grouped metadata", outcome(connection, "SELECT IS_UPDATABLE FROM information_schema.VIEWS "
                    "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='grouped_outer'")))
    results.append(("grouped initial", outcome(connection, grouped_update)))
    for table in ("flags", "aux", "target"):
        run(connection, f"REVOKE SELECT ON probe.{table} FROM inner_owner")
        results.append((f"grouped revoked {table}", outcome(connection, grouped_update)))
        run(connection, f"GRANT SELECT ON probe.{table} TO inner_owner")
    run(connection, "REVOKE SELECT ON probe.flags FROM inner_owner")
    run(connection, "REVOKE SELECT ON probe.aux FROM inner_owner")
    results.append(("grouped revoked flags and aux", outcome(connection, grouped_update)))
    run(connection, "GRANT SELECT ON probe.flags TO inner_owner")
    run(connection, "GRANT SELECT ON probe.aux TO inner_owner")
    for sql in [
        "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW lateral_inner AS "
        "SELECT t.id,t.n,d.aux FROM target t JOIN LATERAL "
        "(SELECT aux FROM aux WHERE aux.id=t.id) d ON d.aux>0",
        "GRANT SELECT,UPDATE ON probe.lateral_inner TO outer_owner",
        "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW lateral_outer "
        "AS SELECT id,n AS metric FROM lateral_inner",
    ]:
        run(connection, sql)
    lateral_update = "UPDATE lateral_outer SET metric=metric+1 WHERE id=1"
    results.append(("lateral metadata", outcome(connection,
                    "SELECT IS_UPDATABLE FROM information_schema.VIEWS "
                    "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='lateral_outer'")))
    results.append(("lateral initial", outcome(connection, lateral_update)))
    for table in ("aux", "target"):
        run(connection, f"REVOKE SELECT ON probe.{table} FROM inner_owner")
        results.append((f"lateral revoked {table}", outcome(connection, lateral_update)))
        run(connection, f"GRANT SELECT ON probe.{table} TO inner_owner")
    run(connection, "CREATE TABLE aux_n(id INT PRIMARY KEY,n INT)")
    run(connection, "INSERT INTO aux_n VALUES(1,7)")
    run(connection, "GRANT SELECT ON probe.aux_n TO inner_owner")
    bare_create = outcome(connection,
        "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW grouped_bare_inner AS "
        "SELECT t.id,t.n,d.flag FROM target t JOIN "
        "((SELECT id,SUM(flag) flag FROM flags GROUP BY id) d "
        "JOIN aux_n a ON n=d.flag) ON t.id=d.id")
    results.append(("grouped bare create", bare_create))
    if bare_create[0] == "ok":
        run(connection, "GRANT SELECT,UPDATE ON probe.grouped_bare_inner TO outer_owner")
        run(connection, "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW grouped_bare_outer "
            "AS SELECT id,n AS metric FROM grouped_bare_inner")
        results.append(("grouped bare metadata", outcome(connection,
            "SELECT IS_UPDATABLE FROM information_schema.VIEWS "
            "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='grouped_bare_outer'")))
        bare_update = "UPDATE grouped_bare_outer SET metric=metric+1 WHERE id=1"
        results.append(("grouped bare initial", outcome(connection, bare_update)))
        for table in ("flags", "aux_n", "target"):
            run(connection, f"REVOKE SELECT ON probe.{table} FROM inner_owner")
            results.append((f"grouped bare revoked {table}", outcome(connection, bare_update)))
            run(connection, f"GRANT SELECT ON probe.{table} TO inner_owner")
        run(connection, "REVOKE SELECT ON probe.flags FROM inner_owner")
        run(connection, "REVOKE SELECT ON probe.aux_n FROM inner_owner")
        results.append(("grouped bare revoked flags and aux_n", outcome(connection, bare_update)))
    json_sources = [
        ("JSON chain", "json_chain",
         "FROM JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j "
         "JOIN JSON_TABLE(JSON_ARRAY(j.x),'$[*]' COLUMNS(y INT PATH '$')) k ON k.y=j.x "
         "JOIN target t ON t.id=k.y"),
        ("JSON sandwich", "json_sandwich",
         "FROM JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j "
         "JOIN target t ON t.id=j.x "
         "JOIN JSON_TABLE(JSON_ARRAY(t.id),'$[*]' COLUMNS(y INT PATH '$')) k ON k.y=t.id"),
    ]
    for label, name, source in json_sources:
        inner, outer = f"{name}_inner", f"{name}_outer"
        for sql in [
            f"CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW {inner} "
            f"AS SELECT t.id,t.n,j.x,k.y {source}",
            f"GRANT SELECT,UPDATE ON probe.{inner} TO outer_owner",
            f"CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW {outer} "
            f"AS SELECT id,n AS metric FROM {inner}",
        ]:
            run(connection, sql)
        update = f"UPDATE {outer} SET metric=metric+1 WHERE id=1"
        results.append((f"{label} metadata", outcome(connection,
                        "SELECT IS_UPDATABLE FROM information_schema.VIEWS "
                        f"WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='{outer}'")))
        results.append((f"{label} initial", outcome(connection, update)))
        for grant, source in [("SELECT", "target"), ("UPDATE", "target"),
                              ("UPDATE", inner)]:
            owner = "inner_owner" if source == "target" else "outer_owner"
            run(connection, f"REVOKE {grant} ON probe.{source} FROM {owner}")
            key = "target" if source == "target" else "component"
            results.append((f"{label} revoked {key} {grant}", outcome(connection, update)))
            if source == "target":
                run(connection, f"GRANT {grant} ON probe.{source} TO {owner}")
    for sql in [
        "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW json_joined_inner AS "
        "SELECT t.id,t.n,j.x FROM target t JOIN "
        "JSON_TABLE(JSON_ARRAY(t.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=t.id "
        "WHERE t.n>=0 WITH CASCADED CHECK OPTION",
        "GRANT SELECT,UPDATE ON probe.json_joined_inner TO outer_owner",
        "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW json_joined_outer AS "
        "SELECT v.id,v.n,v.x,j.k FROM json_joined_inner v "
        "JOIN (SELECT 1 AS k) j ON j.k=v.id WHERE v.n<10 WITH CASCADED CHECK OPTION",
    ]:
        run(connection, sql)
    results.append(("joined JSON layer metadata", outcome(connection,
                    "SELECT IS_UPDATABLE FROM information_schema.VIEWS "
                    "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='json_joined_outer'")))
    results.append(("joined JSON layer update", outcome(connection,
                    "UPDATE json_joined_outer SET n=7 WHERE id=1")))
    results.append(("joined JSON inner check", outcome(connection,
                    "UPDATE json_joined_outer SET n=-1 WHERE id=1")))
    results.append(("joined JSON outer check", outcome(connection,
                    "UPDATE json_joined_outer SET n=10 WHERE id=1")))
    run(connection, "REVOKE SELECT ON probe.target FROM inner_owner")
    results.append(("joined JSON revoked target SELECT", outcome(connection,
                    "UPDATE json_joined_outer SET n=7 WHERE id=1")))
    run(connection, "GRANT SELECT ON probe.target TO inner_owner")
    run(connection, "REVOKE UPDATE ON probe.json_joined_inner FROM outer_owner")
    results.append(("joined JSON revoked component UPDATE", outcome(connection,
                    "UPDATE json_joined_outer SET n=7 WHERE id=1")))
    run(connection, "GRANT UPDATE ON probe.json_joined_inner TO outer_owner")
    run(connection, "GRANT UPDATE ON probe.json_chain_inner TO outer_owner")
    run(connection,
        "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW json_chain_joined_outer AS "
        "SELECT v.id,v.n,v.x,v.y,j.k FROM json_chain_inner v "
        "JOIN (SELECT 1 AS k) j ON j.k=v.id WHERE v.n<10 WITH CASCADED CHECK OPTION")
    results.append(("joined JSON chain metadata", outcome(connection,
                    "SELECT IS_UPDATABLE FROM information_schema.VIEWS "
                    "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='json_chain_joined_outer'")))
    results.append(("joined JSON chain update", outcome(connection,
                    "UPDATE json_chain_joined_outer SET n=8 WHERE id=1")))
    results.append(("joined JSON chain check", outcome(connection,
                    "UPDATE json_chain_joined_outer SET n=10 WHERE id=1")))
    for sql in [
        "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW lateral_joined_inner AS "
        "SELECT t.id,t.n,d.aux FROM target t JOIN LATERAL "
        "(SELECT a.aux FROM aux a WHERE a.id=t.id) d ON d.aux>0 "
        "WHERE t.n>=0 WITH CASCADED CHECK OPTION",
        "GRANT SELECT,UPDATE ON probe.lateral_joined_inner TO outer_owner",
        "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW lateral_joined_outer AS "
        "SELECT v.id,v.n,v.aux,q.mark FROM lateral_joined_inner v "
        "JOIN (SELECT 1 AS mark) q ON q.mark=v.id "
        "WHERE v.n<10 WITH CASCADED CHECK OPTION",
    ]:
        results.append(("joined lateral create", outcome(connection, sql)))
    results.append(("joined lateral metadata", outcome(connection,
                    "SELECT IS_UPDATABLE FROM information_schema.VIEWS "
                    "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='lateral_joined_outer'")))
    for label, value in (("update", 7), ("inner check", -1), ("outer check", 10)):
        results.append((f"joined lateral {label}", outcome(connection,
                        f"UPDATE lateral_joined_outer SET n={value} WHERE id=1")))
    run(connection, "REVOKE SELECT ON probe.aux FROM inner_owner")
    results.append(("joined lateral revoked source SELECT", outcome(connection,
                    "UPDATE lateral_joined_outer SET n=7 WHERE id=1")))
    run(connection, "GRANT SELECT ON probe.aux TO inner_owner")
    run(connection, "REVOKE UPDATE ON probe.lateral_joined_inner FROM outer_owner")
    results.append(("joined lateral revoked component UPDATE", outcome(connection,
                    "UPDATE lateral_joined_outer SET n=7 WHERE id=1")))
    run(connection, "GRANT UPDATE ON probe.lateral_joined_inner TO outer_owner")
    for sql in [
        "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW lateral_chain_inner AS "
        "SELECT t.id,t.n,d.aux,e.bump FROM target t "
        "JOIN LATERAL (SELECT a.aux FROM aux a WHERE a.id=t.id) d ON d.aux>0 "
        "JOIN LATERAL (SELECT d.aux+1 AS bump) e ON e.bump>0 "
        "WHERE t.n>=0 WITH CASCADED CHECK OPTION",
        "GRANT SELECT,UPDATE ON probe.lateral_chain_inner TO outer_owner",
        "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW lateral_chain_outer AS "
        "SELECT v.id,v.n,v.aux,v.bump,d.mark FROM lateral_chain_inner v "
        "JOIN (SELECT 1 AS mark) d ON d.mark=v.id "
        "WHERE v.n<10 WITH CASCADED CHECK OPTION",
    ]:
        results.append(("joined lateral chain create", outcome(connection, sql)))
    results.append(("joined lateral chain metadata", outcome(connection,
                    "SELECT IS_UPDATABLE FROM information_schema.VIEWS "
                    "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='lateral_chain_outer'")))
    results.append(("joined lateral chain update", outcome(connection,
                    "UPDATE lateral_chain_outer SET n=8 WHERE id=1")))
    results.append(("joined lateral chain check", outcome(connection,
                    "UPDATE lateral_chain_outer SET n=10 WHERE id=1")))
    for sql in [
        "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW lateral_chain_bare_inner AS "
        "SELECT t.id,t.n,d.aux,e.bump FROM target t "
        "JOIN LATERAL (SELECT a.aux FROM aux a WHERE a.id=t.id) d ON d.aux>0 "
        "JOIN LATERAL (SELECT aux+1 AS bump) e ON e.bump>0 "
        "WHERE t.n>=0 WITH CASCADED CHECK OPTION",
        "GRANT SELECT,UPDATE ON probe.lateral_chain_bare_inner TO outer_owner",
        "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW lateral_chain_bare_outer AS "
        "SELECT v.id,v.n,v.aux,v.bump,d.mark FROM lateral_chain_bare_inner v "
        "JOIN (SELECT 1 AS mark) d ON d.mark=v.id "
        "WHERE v.n<10 WITH CASCADED CHECK OPTION",
    ]:
        results.append(("joined lateral chain-bare create", outcome(connection, sql)))
    results.append(("joined lateral chain-bare metadata", outcome(connection,
                    "SELECT IS_UPDATABLE FROM information_schema.VIEWS "
                    "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='lateral_chain_bare_outer'")))
    results.append(("joined lateral chain-bare update", outcome(connection,
                    "UPDATE lateral_chain_bare_outer SET n=7 WHERE id=1")))
    for sql in [
        "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW lateral_local_bare_inner AS "
        "SELECT t.id,t.n,d.aux FROM target t "
        "JOIN LATERAL (SELECT aux FROM aux a WHERE a.id=t.id) d ON d.aux>0 "
        "WHERE t.n>=0 WITH CASCADED CHECK OPTION",
        "GRANT SELECT,UPDATE ON probe.lateral_local_bare_inner TO outer_owner",
        "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW lateral_local_bare_outer AS "
        "SELECT v.id,v.n,v.aux,q.mark FROM lateral_local_bare_inner v "
        "JOIN (SELECT 1 AS mark) q ON q.mark=v.id "
        "WHERE v.n<10 WITH CASCADED CHECK OPTION",
    ]:
        results.append(("joined lateral local-bare create", outcome(connection, sql)))
    results.append(("joined lateral local-bare metadata", outcome(connection,
                    "SELECT IS_UPDATABLE FROM information_schema.VIEWS "
                    "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='lateral_local_bare_outer'")))
    results.append(("joined lateral local-bare update", outcome(connection,
                    "UPDATE lateral_local_bare_outer SET n=9 WHERE id=1")))
    for sql in [
        "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW lateral_outer_bare_inner AS "
        "SELECT t.id,t.n,d.aux FROM target t "
        "JOIN LATERAL (SELECT a.aux FROM aux a WHERE a.id=t.id AND n>=0) d ON d.aux>0 "
        "WHERE t.n>=0 WITH CASCADED CHECK OPTION",
        "GRANT SELECT,UPDATE ON probe.lateral_outer_bare_inner TO outer_owner",
        "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW lateral_outer_bare_outer AS "
        "SELECT v.id,v.n,v.aux,q.mark FROM lateral_outer_bare_inner v "
        "JOIN (SELECT 1 AS mark) q ON q.mark=v.id "
        "WHERE v.n<10 WITH CASCADED CHECK OPTION",
    ]:
        results.append(("joined lateral outer-bare create", outcome(connection, sql)))
    results.append(("joined lateral outer-bare metadata", outcome(connection,
                    "SELECT IS_UPDATABLE FROM information_schema.VIEWS "
                    "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='lateral_outer_bare_outer'")))
    results.append(("joined lateral outer-bare update", outcome(connection,
                    "UPDATE lateral_outer_bare_outer SET n=8 WHERE id=1")))
    for sql in [
        "CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW lateral_shadow_inner AS "
        "SELECT t.id,t.n,d.aux FROM target t "
        "JOIN LATERAL (SELECT a.aux FROM aux_shadow a WHERE a.id=t.id AND n>=0) d ON d.aux>0 "
        "WHERE t.n>=0 WITH CASCADED CHECK OPTION",
        "GRANT SELECT,UPDATE ON probe.lateral_shadow_inner TO outer_owner",
        "CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW lateral_shadow_outer AS "
        "SELECT v.id,v.n,v.aux,q.mark FROM lateral_shadow_inner v "
        "JOIN (SELECT 1 AS mark) q ON q.mark=v.id "
        "WHERE v.n<10 WITH CASCADED CHECK OPTION",
    ]:
        results.append(("joined lateral shadow create", outcome(connection, sql)))
    results.append(("joined lateral shadow metadata", outcome(connection,
                    "SELECT IS_UPDATABLE FROM information_schema.VIEWS "
                    "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='lateral_shadow_outer'")))
    results.append(("joined lateral shadow update", outcome(connection,
                    "UPDATE lateral_shadow_outer SET n=9 WHERE id=1")))
    results.append(("final rows", outcome(connection, "SELECT id,n FROM target ORDER BY id")))
    return results


def main():
    if not FSDB.exists():
        raise RuntimeError(f"Build the Release CLI first: {FSDB}")
    with tempfile.TemporaryDirectory(prefix="fsdb-layered-derived-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql-data"
        data.mkdir()
        mysql_socket = str(root / "mysql.sock")
        mysql = fsdb = mysql_connection = fsdb_connection = None
        try:
            with (root / "mysql.log").open("w") as log:
                subprocess.run(
                    ["mysqld", "--no-defaults", "--initialize-insecure", f"--datadir={data}",
                     "--innodb-redo-log-capacity=64M"],
                    check=True, stdout=log, stderr=subprocess.STDOUT,
                )
                mysql = subprocess.Popen(
                    ["mysqld", "--no-defaults", f"--datadir={data}", "--skip-networking",
                     f"--socket={mysql_socket}", f"--pid-file={root / 'mysql.pid'}", "--mysqlx=0",
                     "--innodb-buffer-pool-size=64M", "--innodb-redo-log-capacity=64M"],
                    stdout=log, stderr=subprocess.STDOUT,
                )
                mysql_connection = connect(unix_socket=mysql_socket)
            version = run(mysql_connection, "SELECT VERSION()")[0][0]
            assert version.startswith("8.4.11"), version

            with socket.socket() as reservation:
                reservation.bind(("127.0.0.1", 0))
                port = reservation.getsockname()[1]
            with (root / "fsdb.log").open("w") as log:
                fsdb = subprocess.Popen(
                    ["dotnet", str(FSDB), "--port", str(port)],
                    stdout=log, stderr=subprocess.STDOUT,
                )
                fsdb_connection = connect(host="127.0.0.1", port=port)

            expected = exercise(mysql_connection)
            actual = exercise(fsdb_connection)
            for first, second in zip(expected, actual):
                print("MySQL", first, "FSDB", second)
            return 0 if expected == actual else 1
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
                    ["mysqladmin", "--no-defaults", f"--socket={mysql_socket}",
                     "-uroot", "shutdown"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                )
                mysql.wait(timeout=20)


if __name__ == "__main__":
    raise SystemExit(main())
