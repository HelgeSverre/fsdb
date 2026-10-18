"""Probe ordinary and ngram Boolean FULLTEXT syntax on native MySQL 8.4."""

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


def main():
    with tempfile.TemporaryDirectory(prefix="fsdb-boolean-syntax-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql-data"
        socket = str(root / "mysql.sock")
        server = None
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
                try:
                    with connection.cursor() as cursor:
                        cursor.execute("SELECT VERSION()")
                        version = cursor.fetchone()[0]
                        assert version.startswith("8.4.11"), version
                        cursor.execute("CREATE DATABASE probe")
                        cursor.execute("USE probe")
                        cursor.execute("CREATE TABLE docs(id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft(body))")
                        cursor.execute("INSERT INTO docs VALUES(1,'alpha beta'),(2,'beta gamma')")
                        for query, expected in (
                            ("alpha (", (1064, "syntax error, unexpected $end")),
                            ("alpha )", (1064, "syntax error, unexpected ')', expecting $end")),
                            ("(alpha", (1064, "syntax error, unexpected $end")),
                            ("alpha)", (1064, "syntax error, unexpected ')', expecting $end")),
                            ('"alpha', ((1,),)), ('alpha "', ((1,),)),
                            ('(alpha)', ((1,),)), ('"alpha"', ((1,),)),
                        ):
                            try:
                                cursor.execute(
                                    "SELECT id FROM docs WHERE MATCH(body) AGAINST(%s IN BOOLEAN MODE) ORDER BY id",
                                    (query,),
                                )
                                actual = cursor.fetchall()
                            except pymysql.MySQLError as error:
                                actual = error.args
                            assert actual == expected, (query, actual, expected)

                        cursor.execute(
                            "CREATE TABLE ngram_docs(id INT PRIMARY KEY, body TEXT, "
                            "FULLTEXT KEY ft(body) WITH PARSER ngram)"
                        )
                        cursor.execute("INSERT INTO ngram_docs VALUES(1,'alpha beta'),(2,'beta gamma')")
                        for query in ("alpha (", "alpha )", "(alpha", "alpha)"):
                            cursor.execute(
                                "SELECT id FROM ngram_docs WHERE MATCH(body) AGAINST(%s IN BOOLEAN MODE) ORDER BY id",
                                (query,),
                            )
                            assert cursor.fetchall() == (), query
                        print(f"MySQL {version}: word Boolean groups reject with 1064; ngram groups return no matches")
                finally:
                    connection.close()
            finally:
                if server is not None and server.poll() is None:
                    subprocess.run(
                        ["mysqladmin", "--no-defaults", f"--socket={socket}", "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    server.wait(timeout=20)


if __name__ == "__main__":
    main()
