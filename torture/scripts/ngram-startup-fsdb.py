"""Check fsdb startup and recovery against the ngram-size-oracle.py contract.

Run after building src/Fsdb in Debug. Requires the native mysql client on PATH.
"""

import pathlib
import socket
import subprocess
import tempfile
import time


ROOT = pathlib.Path(__file__).resolve().parents[2]
COMMAND = ["dotnet", str(ROOT / "src/Fsdb/bin/Debug/net10.0/Fsdb.dll")]


def run():
    with tempfile.TemporaryDirectory(prefix="fsdb-ngram-startup-") as temporary:
        directory = pathlib.Path(temporary)
        options = directory / "my.cnf"
        options.write_text("[mysqld]\nngram_token_size=2\n")
        server = None
        port = None

        def sql(statement, check=True):
            return subprocess.run(
                ["mysql", "--no-defaults", "--default-character-set=utf8mb4",
                 "--protocol=tcp", "-h127.0.0.1", f"-P{port}", "-uroot",
                 "--batch", "--raw", "--skip-column-names", "-e", statement],
                text=True, capture_output=True, check=check, timeout=10,
            )

        def expect(statement, expected):
            actual = sql(statement).stdout.strip()
            assert actual == expected, (statement, expected, actual)
            print(f"{statement} -> {actual}", flush=True)

        def stop():
            nonlocal server
            if server is not None:
                if server.poll() is None:
                    server.terminate()
                    try:
                        server.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        server.kill()
                        server.wait(timeout=10)
                server = None

        def start(*arguments):
            nonlocal server, port
            stop()
            with socket.socket() as reservation:
                reservation.bind(("127.0.0.1", 0))
                port = reservation.getsockname()[1]
            log_path = directory / "server.log"
            with log_path.open("w") as log:
                server = subprocess.Popen(
                    COMMAND + ["--defaults-file", str(options), "--data-dir",
                               str(directory / "data"), "--port", str(port)] + list(arguments),
                    stdout=log, stderr=subprocess.STDOUT,
                )
            for _ in range(100):
                if server.poll() is not None:
                    raise RuntimeError(log_path.read_text())
                if sql("SELECT 1", check=False).returncode == 0:
                    return
                time.sleep(0.1)
            raise RuntimeError("fsdb startup timed out: " + log_path.read_text())

        def matches(term):
            return ("SELECT GROUP_CONCAT(id ORDER BY id) FROM fsdb.docs "
                    f"WHERE MATCH(body) AGAINST('{term}' IN BOOLEAN MODE)")

        try:
            help_output = subprocess.check_output(COMMAND + ["--help"], text=True)
            assert "--ngram-token-size" in help_output
            assert subprocess.check_output(COMMAND + ["--version"], text=True).startswith("fsdb ")
            for value in ["abc", "3.0"]:
                rejected = subprocess.run(
                    COMMAND + ["--defaults-file", str(options), "--ngram-token-size", value],
                    text=True, capture_output=True, timeout=10,
                )
                assert rejected.returncode != 0 and "ngram_token_size requires" in rejected.stderr, rejected

            start()
            expect("SELECT @@GLOBAL.ngram_token_size, @@ngram_token_size", "2\t2")
            sql("CREATE TABLE fsdb.docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body) WITH PARSER ngram)")
            sql("INSERT INTO fsdb.docs VALUES(1,'生日快乐'),(2,'生日')")
            start("--ngram-token-size", "3")
            expect("SHOW GLOBAL VARIABLES LIKE 'ngram_token_size'", "ngram_token_size\t3")
            for statement in ["SET GLOBAL ngram_token_size=4", "SELECT @@SESSION.ngram_token_size"]:
                rejected = sql(statement, check=False)
                assert rejected.returncode != 0 and "1238" in rejected.stderr, rejected
            expect(matches("生日"), "1,2")
            expect(matches("生日快"), "NULL")
            sql("INSERT INTO fsdb.docs VALUES(3,'生日快乐')")
            expect(matches("生日快"), "3")
            start("--ngram-token-size", "3")
            expect(matches("生日"), "1,2")
            expect(matches("生日快"), "3")
            sql("ALTER TABLE fsdb.docs DROP INDEX ft")
            sql("ALTER TABLE fsdb.docs ADD FULLTEXT KEY ft(body) WITH PARSER ngram")
            expect(matches("生日快"), "1,3")
            for requested, effective in [("0", "1"), ("-1", "1"), ("11", "10"), ("3k", "10")]:
                start("--ngram-token-size", requested)
                expect("SELECT @@ngram_token_size", effective)
            stop()
            options.write_text("[mysqld]\nloose-ngram-token-size=3\n")
            start()
            expect("SELECT @@ngram_token_size", "3")
        finally:
            stop()
    print("Ngram startup and process-restart checks passed", flush=True)


if __name__ == "__main__":
    run()
