"""Verify stopword startup parsing and restart lifetime on native MySQL 8.4.11."""

import contextlib
import pathlib
import runpy
import subprocess
import tempfile
import time

native = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))
Client, expect = native["Client"], native["expect"]


def check_options():
    cases = [(None, True), ("ON", True), ("TrUe", True), ("1", True),
             ("OFF", False), ("false", False), ("0", False), ("2", False),
             ("-1", False), ("", False), ("yes", False), ("01", False), (" ON ", False)]
    arguments = [(["--innodb-ft-enable-stopword" + ("=" + value if value is not None else "")], enabled)
                 for value, enabled in cases]
    arguments += [(["--" + prefix + "innodb-ft-enable-stopword=OFF"], enabled)
                  for prefix, enabled in [("skip-", False), ("disable-", False), ("enable-", True)]]
    arguments += [(["--skip-innodb-ft-enable-stopword", "--innodb-ft-enable-stopword=ON"], True),
                  (["--innodb-ft-enable-stopword=ON", "--skip-innodb-ft-enable-stopword"], False)]
    for options, enabled in arguments:
        result = subprocess.run(["mysqld", "--no-defaults", *options, "--verbose", "--help"],
                                capture_output=True, text=True, check=True)
        value = next(line.split()[-1] for line in result.stdout.splitlines()
                     if line.startswith("innodb-ft-enable-stopword "))
        expect(" ".join(options), value, "TRUE" if enabled else "FALSE")


@contextlib.contextmanager
def server(data, socket, log, value, options=()):
    process = subprocess.Popen(
        ["mysqld", "--no-defaults", "--datadir=" + str(data), "--socket=" + socket,
         "--skip-networking", "--mysqlx=0", "--innodb-buffer-pool-size=64M",
         "--innodb-redo-log-capacity=64M", "--innodb-ft-enable-stopword=" + value, *options],
        stdout=log, stderr=subprocess.STDOUT)
    try:
        for _ in range(150):
            if process.poll() is not None:
                raise RuntimeError("MySQL exited during startup")
            ready = subprocess.run(["mysqladmin", "--no-defaults", "--socket=" + socket,
                                    "-uroot", "ping"], capture_output=True)
            if ready.returncode == 0:
                break
            time.sleep(0.1)
        else:
            raise TimeoutError("MySQL startup")
        with contextlib.closing(Client(["--socket=" + socket])) as client:
            yield client
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)


def check_restart():
    with tempfile.TemporaryDirectory(prefix="fsdb-stopword-startup-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        socket = str(root / "mysql.sock")
        log_path = root / "server.log"
        try:
            with log_path.open("w") as log:
                subprocess.run(["mysqld", "--no-defaults", "--initialize-insecure",
                                "--datadir=" + str(data), "--innodb-redo-log-capacity=64M"],
                               stdout=log, stderr=subprocess.STDOUT, check=True)
                for startup, expected in [("OFF", "0"), ("ON", "1"), ("2", "0")]:
                    with server(data, socket, log, startup) as client:
                        expect("version", client.query("SELECT VERSION()"), "8.4.11")
                        expect("startup scopes " + startup, client.query(
                            "SELECT @@GLOBAL.innodb_ft_enable_stopword,@@SESSION.innodb_ft_enable_stopword"),
                            expected + "\t" + expected)
                        client.query("CREATE DATABASE IF NOT EXISTS probe;"
                                     "CREATE TABLE IF NOT EXISTS probe.retained(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body));"
                                     "INSERT IGNORE INTO probe.retained VALUES(1,'the'),(2,'zzzz')")
                        expect("retained policy after startup " + startup, client.query(
                            "SELECT GROUP_CONCAT(id) FROM probe.retained WHERE MATCH(body) AGAINST('the' IN BOOLEAN MODE)"), "1")
                        client.query("DROP TABLE IF EXISTS probe.fresh;"
                                     "CREATE TABLE probe.fresh(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body));"
                                     "INSERT INTO probe.fresh VALUES(1,'the'),(2,'zzzz')")
                        expect("new index startup " + startup, client.query(
                            "SELECT GROUP_CONCAT(id) FROM probe.fresh WHERE MATCH(body) AGAINST('the' IN BOOLEAN MODE)"),
                            "NULL" if expected == "1" else "1")
        except Exception:
            print(log_path.read_text(), flush=True)
            raise


def custom_startup_cases():
    for variable in ["user", "server"]:
        for value in [None, "", "probe/words", "missing", "NULL", "123"]:
            option = "--innodb-ft-" + variable + "-stopword-table" + ("=" + value if value is not None else "")
            yield [option], value if variable == "user" else None, value if variable == "server" else None
    yield ["--innodb-ft-server-stopword-table=probe/words", "--innodb-ft-user-stopword-table=missing"], "missing", "probe/words"
    yield ["--innodb-ft-user-stopword-table=missing", "--innodb-ft-user-stopword-table=probe/words"], "probe/words", None


def verify_custom_startup(client, user, server_source):
    expect("startup source scopes", client.query(
        "SELECT COALESCE(CONCAT('[',@@GLOBAL.innodb_ft_user_stopword_table,']'),'absent'),"
        "COALESCE(CONCAT('[',@@SESSION.innodb_ft_user_stopword_table,']'),'absent'),"
        "COALESCE(CONCAT('[',@@GLOBAL.innodb_ft_server_stopword_table,']'),'absent')"),
        "\t".join("absent" if value is None else "[" + value + "]" for value in [user, user, server_source]))
    client.query("CREATE DATABASE IF NOT EXISTS probe;CREATE TABLE IF NOT EXISTS probe.words(value VARCHAR(30));"
                 "DELETE FROM probe.words;INSERT INTO probe.words VALUES('orchard');"
                 "DROP TABLE IF EXISTS probe.docs;"
                 "CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body));"
                 "INSERT INTO probe.docs VALUES(1,'orchard'),(2,'the'),(3,'cobalt')")
    custom = (user if user is not None else server_source) == "probe/words"
    for word, expected in [("orchard", "NULL" if custom else "1"), ("the", "2" if custom else "NULL"), ("cobalt", "3")]:
        expect("startup source " + word, client.query(
            "SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs WHERE MATCH(body) AGAINST('" + word + "')"), expected)


def check_custom_startup():
    with tempfile.TemporaryDirectory(prefix="fsdb-custom-stopword-startup-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        log_path = root / "server.log"
        try:
            with log_path.open("w") as log:
                subprocess.run(["mysqld", "--no-defaults", "--initialize-insecure",
                                "--datadir=" + str(data), "--innodb-redo-log-capacity=64M",
                                "--innodb-buffer-pool-size=64M"], stdout=log, stderr=subprocess.STDOUT, check=True)
                for options, user, server_source in custom_startup_cases():
                    with server(data, str(root / "mysql.sock"), log, "ON", options) as client:
                        print(" ".join(options), flush=True)
                        expect("version", client.query("SELECT VERSION()"), "8.4.11")
                        verify_custom_startup(client, user, server_source)
        except Exception:
            print(log_path.read_text(), flush=True)
            raise


if __name__ == "__main__":
    check_options()
    check_restart()
    check_custom_startup()
    print("Full-text stopword startup oracle passed", flush=True)
