"""Compare multi-column CJK ngram searches with native MySQL 8.4.11.

Build Fsdb.Cli in Release first. Requires native mysqld/mysqladmin and PyMySQL.
Each token size uses disposable servers and data directories.
"""

import math
import pathlib
import socket
import subprocess
import sys
import tempfile
import time

import pymysql


REPO = pathlib.Path(__file__).resolve().parents[2]
FSDB = REPO / "src/Fsdb.Cli/bin/Release/net10.0/Fsdb.Cli.dll"

DOCUMENTS = [
    ("生日快乐", "祝福"), ("生日", "快乐"), ("中文", "生日"),
    ("日本語", "韓国語"), ("生日　快乐", "東京"), ("🙂生日", "微笑"),
    ("生🙂日", "快乐"), ("ab生日", "ことば"), ("生日abc快乐", "火曜日"),
    ("快乐", "生日快乐"), ("今日は晴れ", "日本 語"), ("한국어", "생일 축하"),
    ("生日，快乐", "中文"), ("生日!快乐", "日本語"), ("", "生日"),
    ("生日", None), ("中，文", "快乐"), ("𠮷野家", "𠮷"),
    ("生日\u200d快乐", "连字"), ("生日\ufe0f快乐", "变体"),
    ("生e\u0301日", "组合"), ("生日\n快乐", "换行"),
    ("生日。快乐", "句号"), ("生日\u00a0快乐", "空格"),
    ("한글한", "자모"), ("👩\u200d💻生日", "表情"),
]

TERMS = [
    "生日快乐", "生日", "快乐", "生", "生日*", "生*", "中文", "日本語",
    "日本 語", "한국어", "생일 축하", "🙂生日", "生🙂日", "𠮷野家", "𠮷",
    "中，文", "生日，快乐", "生日!快乐", '"生日 快乐"', '"生日，快乐"',
    "+生日 +快乐", "生日 -快乐", "生日* +快乐", "ab生日", "abc", "ことば",
    "東京", "祝福", "火曜日", "生日 日本語", "+生日 -日本語", '"生日快乐"',
    "生 日", "生　日", "生日生日", "日本語日本語", "生日生日 日本語",
    "生日快乐生日快乐", "生日日生", "한국어한국어",
    "生日\u200d快乐", "生日\ufe0f快乐", "生e\u0301日", "生日\n快乐",
    "生日。快乐", "生日\u00a0快乐", "한글한", "👩\u200d💻生日",
]

MODES = ["IN NATURAL LANGUAGE MODE", "IN BOOLEAN MODE", "WITH QUERY EXPANSION"]


def connect(**options):
    for _ in range(100):
        try:
            return pymysql.connect(user="root", autocommit=True, **options)
        except pymysql.MySQLError:
            time.sleep(0.1)
    raise RuntimeError("database server did not become ready")


def query(connection, sql, args=None):
    with connection.cursor() as cursor:
        cursor.execute(sql, args)
        return cursor.fetchall()


def setup(connection, size):
    reported = query(connection, "SELECT @@ngram_token_size")[0][0]
    assert reported == size, (reported, size)
    for sql in [
        "CREATE DATABASE probe", "USE probe",
        "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,title TEXT,"
        "FULLTEXT KEY ft(body,title) WITH PARSER ngram)",
    ]:
        query(connection, sql)
    for index, (body, title) in enumerate(DOCUMENTS, 1):
        query(connection, "INSERT INTO docs VALUES(%s,%s,%s)", (index, body, title))


def result(connection, term, mode):
    sql = f"SELECT id FROM docs WHERE MATCH(body,title) AGAINST(%s {mode}) ORDER BY id"
    try:
        return tuple(row[0] for row in query(connection, sql, (term,)))
    except pymysql.MySQLError as error:
        return "error", error.args[0]


def scored_result(connection, term, mode):
    score = f"MATCH(body,title) AGAINST(%s {mode})"
    sql = f"SELECT id,{score} AS relevance FROM docs WHERE {score} ORDER BY relevance DESC,id"
    try:
        return tuple((row[0], float(row[1])) for row in query(connection, sql, (term, term)))
    except pymysql.MySQLError as error:
        return "error", error.args[0]


def scores_agree(expected, actual):
    expected_error = bool(expected) and expected[0] == "error"
    actual_error = bool(actual) and actual[0] == "error"
    if expected_error or actual_error:
        return expected == actual
    if len(expected) != len(actual):
        return False
    actual_scores = dict(actual)
    if len(actual_scores) != len(expected):
        return False
    if not all(
        id in actual_scores and math.isclose(score, actual_scores[id], rel_tol=1e-5, abs_tol=1e-5)
        for id, score in expected
    ):
        return False
    positions = {id: position for position, (id, _) in enumerate(actual)}
    return all(
        positions[first_id] < positions[second_id]
        for position, (first_id, first_score) in enumerate(expected)
        for second_id, second_score in expected[position + 1:]
        if first_score > second_score and not math.isclose(first_score, second_score, rel_tol=1e-5, abs_tol=1e-5)
    )


def verify(size):
    with tempfile.TemporaryDirectory(prefix=f"fsdb-ngram-{size}-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql"
        data.mkdir()
        mysql_socket = str(root / "mysql.sock")
        mysql = fsdb = mysql_connection = fsdb_connection = None
        with (root / "mysql.log").open("w") as mysql_log, (root / "fsdb.log").open("w") as fsdb_log:
            try:
                subprocess.run(
                    ["mysqld", "--no-defaults", "--initialize-insecure", f"--datadir={data}",
                     "--innodb-redo-log-capacity=64M"],
                    check=True, stdout=mysql_log, stderr=subprocess.STDOUT,
                )
                mysql = subprocess.Popen(
                    ["mysqld", "--no-defaults", f"--datadir={data}", "--skip-networking",
                     f"--socket={mysql_socket}", f"--pid-file={root / 'mysql.pid'}",
                     "--mysqlx=0", "--innodb-buffer-pool-size=64M",
                     "--innodb-redo-log-capacity=64M", f"--ngram-token-size={size}"],
                    stdout=mysql_log, stderr=subprocess.STDOUT,
                )
                mysql_connection = connect(unix_socket=mysql_socket)
                version = query(mysql_connection, "SELECT VERSION()")[0][0]
                assert version.startswith("8.4.11"), version

                with socket.socket() as reservation:
                    reservation.bind(("127.0.0.1", 0))
                    port = reservation.getsockname()[1]
                fsdb = subprocess.Popen(
                    ["dotnet", str(FSDB), "--port", str(port), "--ngram-token-size", str(size)],
                    stdout=fsdb_log, stderr=subprocess.STDOUT,
                )
                fsdb_connection = connect(host="127.0.0.1", port=port)
                for connection in (mysql_connection, fsdb_connection):
                    setup(connection, size)

                mismatches = []
                for mode in MODES:
                    for term in TERMS:
                        expected = result(mysql_connection, term, mode)
                        actual = result(fsdb_connection, term, mode)
                        if actual != expected:
                            mismatches.append((mode, term, expected, actual))
                        expected_scores = scored_result(mysql_connection, term, mode)
                        actual_scores = scored_result(fsdb_connection, term, mode)
                        if not scores_agree(expected_scores, actual_scores):
                            mismatches.append((mode, term, "relevance", expected_scores, actual_scores))
                print(f"size {size}: {len(MODES) * len(TERMS)} queries; {len(mismatches)} mismatches")
                for mismatch in mismatches:
                    print(mismatch)
                return len(mismatches)
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
                        ["mysqladmin", "--no-defaults", f"--socket={mysql_socket}", "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    mysql.wait(timeout=20)


if __name__ == "__main__":
    if not FSDB.exists():
        raise RuntimeError(f"Build the Release CLI first: {FSDB}")
    sizes = [int(value) for value in sys.argv[1:]] or list(range(1, 11))
    sys.exit(1 if sum(verify(size) for size in sizes) else 0)
