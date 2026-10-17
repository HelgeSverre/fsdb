"""Probe MySQL 8.4 secondary-index ordered limited-write wait boundaries."""

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
    with tempfile.TemporaryDirectory(prefix="fsdb-secondary-frontier-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        socket = str(root / "mysql.sock")
        server = owner = None
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
                owner = connect(socket)
                with owner.cursor() as cursor:
                    cursor.execute("SELECT VERSION()")
                    version = cursor.fetchone()[0]
                    assert version.startswith("8.4."), version
                    cursor.execute("CREATE DATABASE probe")
                    cursor.execute("USE probe")
                base_cases = (
                    *[(1, size) for size in (1, 2, 3, 4, 8)],
                    *[(2, size) for size in (2, 3, 4, 5, 8, 16, 32, 64, 128, 256)],
                    *[(3, size) for size in (3, 4, 5, 6, 8)],
                    *[(4, size) for size in (4, 5, 6, 7, 8, 9)],
                )
                cases = [(limit, size, "n>=0", size < 2 * limit, size + 1, "UPDATE")
                         for limit, size in base_cases]
                cases += [(2, 3, "", True, 4, "UPDATE"),
                          (2, 3, "id<=2", False, 4, "UPDATE"),
                          (2, 3, "rank_value<=2", False, 4, "UPDATE"),
                          (2, 3, "id<=2", False, 0, "UPDATE"),
                          (2, 3, "id<=2", False, 0, "DELETE")]
                for limit, size, predicate, should_wait, inserted_rank, verb in cases:
                    waiter = connect(socket)
                    try:
                        with owner.cursor() as cursor:
                            cursor.execute("DROP TABLE IF EXISTS target")
                            cursor.execute("CREATE TABLE target(id INT PRIMARY KEY,rank_value INT,n INT,KEY ix_rank(rank_value))")
                            cursor.executemany("INSERT INTO target VALUES(%s,%s,0)", [(i, i) for i in range(1, size + 1)])
                            cursor.execute("BEGIN")
                            cursor.execute("INSERT INTO target VALUES(%s,%s,0)", (size + 1, inserted_rank))
                        with waiter.cursor() as cursor:
                            cursor.execute("USE probe")
                            cursor.execute("BEGIN")
                        started = threading.Event()
                        result = {}

                        def write():
                            try:
                                with waiter.cursor() as cursor:
                                    started.set()
                                    where = f" WHERE {predicate}" if predicate else ""
                                    command = "UPDATE target SET n=n+1" if verb == "UPDATE" else "DELETE FROM target"
                                    cursor.execute(f"{command}{where} ORDER BY rank_value ASC LIMIT {limit}")
                                    result["affected"] = cursor.rowcount
                                    cursor.execute("COMMIT")
                            except Exception as error:
                                result["error"] = str(error)

                        thread = threading.Thread(target=write, daemon=True)
                        thread.start()
                        assert started.wait(2)
                        thread.join(0.25)
                        waited = thread.is_alive()
                        owner.commit()
                        thread.join(5)
                        assert not thread.is_alive(), (size, result)
                        assert result == {"affected": limit}, (limit, size, result)
                        assert waited == should_wait, (limit, size, predicate, waited)
                        with owner.cursor() as cursor:
                            cursor.execute("SELECT id,n FROM target ORDER BY id")
                            final_rows = cursor.fetchall()
                        expected_rows = (tuple((i, int(i <= limit)) for i in range(1, size + 2))
                                         if verb == "UPDATE" else
                                         tuple((i, 0) for i in range(limit + 1, size + 2)))
                        assert final_rows == expected_rows, (limit, size, predicate, final_rows)
                        print(f"MySQL {version}: {verb} limit={limit} visible_rows={size} predicate={predicate or 'none'} inserted_rank={inserted_rank} wait={waited}", flush=True)
                    finally:
                        waiter.close()
            finally:
                if owner:
                    owner.close()
                if server and server.poll() is None:
                    subprocess.run(
                        ["mysqladmin", "--no-defaults", f"--socket={socket}", "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    server.wait(timeout=20)


if __name__ == "__main__":
    main()
