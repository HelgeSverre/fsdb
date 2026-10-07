"""Verify lazy custom-stopword loading after restart on native MySQL 8.4.11."""

import contextlib
import pathlib
import runpy
import subprocess
import tempfile

startup = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-stopword-startup-oracle.py")))
expect = startup["expect"]


@contextlib.contextmanager
def database():
    with tempfile.TemporaryDirectory(prefix="fsdb-stopword-loading-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        log_path = root / "server.log"
        try:
            with log_path.open("w") as log:
                subprocess.run(["mysqld", "--no-defaults", "--initialize-insecure",
                                "--datadir=" + str(data), "--innodb-redo-log-capacity=64M",
                                "--innodb-buffer-pool-size=64M"], stdout=log, stderr=subprocess.STDOUT, check=True)
                yield lambda: startup["server"](data, str(root / "mysql.sock"), log, "ON")
        except Exception:
            print(log_path.read_text(), flush=True)
            raise


def seed(client, names):
    expect("version", client.query("SELECT VERSION()"), "8.4.11")
    client.query("CREATE DATABASE probe;CREATE TABLE probe.words(value VARCHAR(30));"
                 "INSERT INTO probe.words VALUES('orchard');"
                 "SET SESSION innodb_ft_user_stopword_table='probe/words'")
    for name in names:
        client.query("CREATE TABLE probe." + name +
                     "(id INT PRIMARY KEY,body TEXT,other TEXT,FULLTEXT ft(body));"
                     "INSERT INTO probe." + name +
                     " VALUES(1,'orchard','orchard'),(2,'cobalt','cobalt'),(3,'the','the')")
    client.query("DELETE FROM probe.words;INSERT INTO probe.words VALUES('cobalt')")


def matching(client, table, column, word):
    return client.query("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe." + table +
                        " WHERE MATCH(" + column + ") AGAINST('" + word + "')")


def check_first_access():
    cases = [
        ("cold", None, False),
        ("plain", "SELECT * FROM probe.{table}", False),
        ("counted", "SELECT COUNT(*) FROM probe.{table}", False),
        ("searched", "SELECT id FROM probe.{table} WHERE MATCH(body) AGAINST('orchard')", True),
        ("empty_search", "SELECT id FROM probe.{table} WHERE MATCH(body) AGAINST('nomatch')", True),
        ("inserted", "INSERT INTO probe.{table} VALUES(4,'zzzz','zzzz')", True),
        ("empty_update", "UPDATE probe.{table} SET body='zzzz' WHERE id=999", False),
        ("same_update", "UPDATE probe.{table} SET body=body WHERE id=1", False),
        ("changed_id", "UPDATE probe.{table} SET id=4 WHERE id=1", False),
        ("empty_delete", "DELETE FROM probe.{table} WHERE id=999", False),
        ("deleted", "DELETE FROM probe.{table} WHERE id=1", False),
        ("renamed", "ALTER TABLE probe.{table} RENAME INDEX ft TO renamed", False),
        ("ordinary_index", "ALTER TABLE probe.{table} ADD INDEX normal(body(8))", False),
        ("commented", "ALTER TABLE probe.{table} COMMENT='changed'", False),
    ]
    with database() as server:
        with server() as client:
            seed(client, [name for name, _, _ in cases])
        with server() as client:
            for name, operation, loads in cases:
                if operation:
                    client.query(operation.format(table=name))
                client.query("ALTER TABLE probe." + name + " ADD FULLTEXT ft_other(other)")
                expect(name + " added index", matching(client, name, "other", "cobalt"), "NULL" if loads else "2")


def check_source_edits_after_restart():
    with database() as server:
        with server() as client:
            seed(client, ["cold", "searched", "altered"])
        with server() as client:
            client.query("SELECT id FROM probe.searched WHERE MATCH(body) AGAINST('nomatch');"
                         "ALTER TABLE probe.altered ADD FULLTEXT ft_other(other);"
                         "DELETE FROM probe.words;INSERT INTO probe.words VALUES('the')")
            for name in ["cold", "searched", "altered"]:
                client.query("INSERT INTO probe." + name + " VALUES(5,'orchard cobalt the','orchard cobalt the')")
                expected = ["5", "2", "3,5"] if name == "searched" else ["5", "2,5", "3"]
                for word, ids in zip(["orchard", "cobalt", "the"], expected):
                    expect(name + " source edit " + word, matching(client, name, "body", word), ids)


if __name__ == "__main__":
    check_first_access()
    check_source_edits_after_restart()
    print("Lazy custom-stopword loading oracle passed", flush=True)
