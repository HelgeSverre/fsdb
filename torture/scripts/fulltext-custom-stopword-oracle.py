"""Verify custom stopword validation, capture, and restart lifetime on MySQL 8.4.11."""

import contextlib
import pathlib
import runpy
import subprocess
import tempfile

scripts = pathlib.Path(__file__).parent
native = runpy.run_path(str(scripts / "fulltext-transaction-oracle.py"))
startup = runpy.run_path(str(scripts / "fulltext-stopword-startup-oracle.py"))
expect = native["expect"]


def connection_options(client):
    return [argument for argument in client.process.args if argument.startswith(("--socket=", "--protocol=", "-h", "-P"))]


def matching(client, term, table="docs", mode="IN BOOLEAN MODE", column="body"):
    return client.query(f"SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.{table} "
                        f"WHERE MATCH({column}) AGAINST('{term}' {mode})")


def word_matches(client, label, expected):
    for term, ids in zip(["orchard", "cobalt", "the"], expected):
        for mode in ["IN BOOLEAN MODE", "IN NATURAL LANGUAGE MODE"]:
            expect(f"{label}: {term} {mode}", matching(client, term, mode=mode), ids)


def seed(client):
    client.query("DROP TABLE IF EXISTS probe.docs;"
                 "CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body));"
                 "INSERT INTO probe.docs VALUES(1,'orchard'),(2,'cobalt'),(3,'the'),(4,'zzzz')")


def validation(client, other):
    expect("default custom settings", client.query(
        "SELECT @@GLOBAL.innodb_ft_server_stopword_table,"
        "@@GLOBAL.innodb_ft_user_stopword_table,@@SESSION.innodb_ft_user_stopword_table"), "NULL\tNULL\tNULL")
    client.query("DELIMITER //\n"
                 "CREATE PROCEDURE probe.attempt(IN statement_text TEXT) BEGIN "
                 "DECLARE EXIT HANDLER FOR SQLEXCEPTION BEGIN "
                 "GET DIAGNOSTICS CONDITION 1 @code=MYSQL_ERRNO,@state=RETURNED_SQLSTATE; "
                 "SELECT @code,@state; END; SET @statement=statement_text; "
                 "PREPARE checked FROM @statement; EXECUTE checked; DEALLOCATE PREPARE checked; "
                 "SELECT 0; END//\nDELIMITER ;\n")

    def attempt(statement, expected):
        quoted = statement.replace("'", "''")
        expect(statement, client.query(f"CALL probe.attempt('{quoted}')"), expected)

    attempt("SELECT @@SESSION.innodb_ft_server_stopword_table", "1238\tHY000")
    attempt("SET SESSION innodb_ft_server_stopword_table=NULL", "1229\tHY000")
    attempt("SET SESSION innodb_ft_server_stopword_table=123", "1229\tHY000")
    attempt("SET SESSION innodb_ft_server_stopword_table='missing'", "1229\tHY000")
    for variable, scope in [("innodb_ft_user_stopword_table", "SESSION"),
                            ("innodb_ft_server_stopword_table", "GLOBAL")]:
        for value, expected in [("NULL", "0"), ("''", "1231\t42000"),
                                ("'probe/missing'", "1231\t42000"),
                                ("'probe.docs'", "1231\t42000"), ("123", "1232\t42000")]:
            attempt(f"SET {scope} {variable}={value}", expected)

    for name, columns, engine, expected in [
        ("valid", "value VARCHAR(30)", "InnoDB", "0"),
        ("upper", "VALUE VARCHAR(30)", "InnoDB", "1231\t42000"),
        ("extra", "value VARCHAR(30),id INT", "InnoDB", "0"),
        ("second", "id INT,value VARCHAR(30)", "InnoDB", "1231\t42000"),
        ("charcol", "value CHAR(30)", "InnoDB", "1231\t42000"),
        ("textcol", "value TEXT", "InnoDB", "1231\t42000"),
        ("intcol", "value INT", "InnoDB", "1231\t42000"),
        ("binarycol", "value VARBINARY(30)", "InnoDB", "1231\t42000"),
        ("otherengine", "value VARCHAR(30)", "MyISAM", "1231\t42000"),
    ]:
        client.query(f"CREATE TABLE probe.{name}({columns}) ENGINE={engine}")
        attempt(f"SET SESSION innodb_ft_user_stopword_table='probe/{name}'", expected)
    client.query("CREATE TEMPORARY TABLE probe.temporary_words(value VARCHAR(30));"
                 "CREATE VIEW probe.view_words AS SELECT value FROM probe.valid")
    for name in ["temporary_words", "view_words"]:
        attempt(f"SET SESSION innodb_ft_user_stopword_table='probe/{name}'", "1231\t42000")

    client.query("SET GLOBAL innodb_ft_user_stopword_table='probe/valid'")
    expect("GLOBAL leaves existing session alone", other.query(
        "SELECT @@SESSION.innodb_ft_user_stopword_table,@@GLOBAL.innodb_ft_user_stopword_table"), "NULL\tprobe/valid")
    with contextlib.closing(native["Client"](connection_options(client))) as fresh:
        expect("GLOBAL seeds new session", fresh.query(
            "SELECT @@SESSION.innodb_ft_user_stopword_table,@@GLOBAL.innodb_ft_user_stopword_table"), "probe/valid\tprobe/valid")
    client.query("SET GLOBAL innodb_ft_user_stopword_table=NULL;SET SESSION innodb_ft_user_stopword_table=NULL")
    expect("SHOW renders unset table settings as empty text", client.query(
        "SHOW SESSION VARIABLES LIKE 'innodb_ft_%stopword_table'"),
        "innodb_ft_server_stopword_table\t\ninnodb_ft_user_stopword_table\t")
    client.query("SET GLOBAL innodb_ft_server_stopword_table='probe/valid'")
    expect("SHOW SESSION reads the global server source", client.query(
        "SHOW SESSION VARIABLES LIKE 'innodb_ft_server_stopword_table'"), "innodb_ft_server_stopword_table\tprobe/valid")
    client.query("SET GLOBAL innodb_ft_server_stopword_table=NULL")



def precedence(client):
    client.query("CREATE TABLE probe.user_words(value VARCHAR(30));INSERT INTO probe.user_words VALUES('orchard');"
                 "CREATE TABLE probe.server_words(value VARCHAR(30));INSERT INTO probe.server_words VALUES('cobalt');"
                 "SET GLOBAL innodb_ft_server_stopword_table='probe/server_words'")
    cases = [
        ("server only", "", ["1", "NULL", "3"]),
        ("user overrides server", "SET SESSION innodb_ft_user_stopword_table='probe/user_words'", ["NULL", "2", "3"]),
        ("disabled bypasses both", "SET SESSION innodb_ft_enable_stopword=OFF", ["1", "2", "3"]),
        ("NULL user restores server", "SET SESSION innodb_ft_enable_stopword=ON;SET SESSION innodb_ft_user_stopword_table=NULL", ["1", "NULL", "3"]),
        ("missing user bypasses server", "SET SESSION innodb_ft_user_stopword_table='probe/user_words';DROP TABLE probe.user_words", ["1", "2", "NULL"]),
        ("missing user without server", "SET GLOBAL innodb_ft_server_stopword_table=NULL", ["1", "2", "NULL"]),
        ("both NULL use builtin", "SET SESSION innodb_ft_user_stopword_table=NULL", ["1", "2", "NULL"]),
        ("empty custom replaces builtin", "CREATE TABLE probe.empty_words(value VARCHAR(30));SET SESSION innodb_ft_user_stopword_table='probe/empty_words'", ["1", "2", "3"]),
    ]
    for label, change, expected in cases:
        if change:
            client.query(change)
        seed(client)
        word_matches(client, label, expected)


def capture_and_rebuild(client):
    client.query("CREATE TABLE probe.life_words(value VARCHAR(30));INSERT INTO probe.life_words VALUES('orchard');"
                 "SET SESSION innodb_ft_user_stopword_table='probe/life_words'")
    seed(client)
    client.query("DELETE FROM probe.life_words;INSERT INTO probe.life_words VALUES('cobalt');"
                 "INSERT INTO probe.docs VALUES(5,'orchard cobalt the')")
    word_matches(client, "source edits and later inserts retain captured policy", ["NULL", "2,5", "3,5"])
    client.query("ALTER TABLE probe.docs ENGINE=InnoDB")
    word_matches(client, "rebuild loads changed source", ["1,5", "NULL", "3,5"])


def collation_and_contents(client):
    collations = ["utf8mb4_0900_ai_ci", "utf8mb4_0900_as_cs", "utf8mb4_bin"]
    for source in collations:
        for document in collations:
            client.query("SET SESSION innodb_ft_user_stopword_table=NULL;DROP TABLE IF EXISTS probe.words;"
                         "DROP TABLE probe.docs;"
                         f"CREATE TABLE probe.words(value VARCHAR(30) COLLATE {source});"
                         "INSERT INTO probe.words VALUES('Orchard'),('café');"
                         "SET SESSION innodb_ft_user_stopword_table='probe/words';"
                         f"CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT COLLATE {document},FULLTEXT ft(body));"
                         "INSERT INTO probe.docs VALUES(1,'orchard'),(2,'Orchard'),(3,'ORCHARD'),(4,'cafe'),(5,'café'),(6,'CAFÉ'),(7,'zzzz')")
            expected = (["NULL"] * 6 if source == collations[0] else
                        ["1,2,3"] * 3 + ["4"] * 3 if document == collations[0] else
                        ["1,2,3"] * 3 + ["4", "NULL", "NULL"] if document == collations[1] else
                        ["1", "NULL", "3", "4", "NULL", "6"])
            for term, ids in zip(["orchard", "Orchard", "ORCHARD", "cafe", "café", "CAFÉ"], expected):
                expect(f"source {source}, document {document}, {term}", matching(client, term), ids)

    client.query("SET SESSION innodb_ft_user_stopword_table=NULL;DROP TABLE probe.words;"
                 "CREATE TABLE probe.words(value VARCHAR(30));"
                 "INSERT INTO probe.words VALUES(NULL),(''),('orchard '),('cobalt red');"
                 "SET SESSION innodb_ft_user_stopword_table='probe/words'")
    seed(client)
    word_matches(client, "NULL empty whitespace and phrase entries", ["1", "2", "3"])
    client.query("DELETE FROM probe.words;INSERT INTO probe.words VALUES('a');DROP TABLE probe.docs;"
                 "CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body) WITH PARSER ngram);"
                 "INSERT INTO probe.docs VALUES(1,'ab'),(2,'bc'),(3,'cd'),(4,'zz')")
    for word, expected in [("a", ["NULL", "2", "3"]), ("abc", ["1", "2", "3"])]:
        client.query(f"DELETE FROM probe.words;INSERT INTO probe.words VALUES('{word}');ALTER TABLE probe.docs ENGINE=InnoDB")
        for term, ids in zip(["ab", "bc", "cd"], expected):
            expect(f"ngram stopword {word}, query {term}", matching(client, term), ids)


def phrases(client):
    client.query("DELETE FROM probe.words;INSERT INTO probe.words VALUES('orchard');DROP TABLE probe.docs;"
                 "CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body));"
                 "INSERT INTO probe.docs VALUES(1,'orchard cobalt'),(2,'other cobalt'),(3,'the cobalt'),(4,'orchard the cobalt'),(5,'zzzz')")
    for term, boolean, natural in [
        ("orchard", "NULL", "NULL"), ("the", "3,4", "3,4"),
        ('"orchard cobalt"', "1,2,3,4", "1,2,3,4"),
        ('"the cobalt"', "3,4", "3,4"), ('"orchard the cobalt"', "3,4", "3,4"),
        ("+orchard +cobalt", "NULL", "1,2,3,4"), ("orch*", "NULL", "NULL"),
    ]:
        for mode, ids in [("IN BOOLEAN MODE", boolean), ("IN NATURAL LANGUAGE MODE", natural)]:
            expect(f"custom phrase {term} {mode}", matching(client, term, mode=mode), ids)


def ngram_source_collation(client):
    for source, word, expected in [
        ("utf8mb4_0900_ai_ci", "A", "5,6,7"),
        ("utf8mb4_0900_ai_ci", "ss", "1,2,3,4,7"),
        ("utf8mb4_0900_ai_ci", "ß", "1,2,3,4,7"),
        ("utf8mb4_0900_as_cs", "A", "1,2,3,4,5,6,7"),
        ("utf8mb4_bin", "a ", "4,5,6,7"),
        ("utf8mb4_0900_ai_ci", "a ", "1,2,3,4,5,6,7"),
        ("utf8mb4_0900_ai_ci", "abc", "1,2,3,4,5,6,7"),
    ]:
        client.query("SET SESSION innodb_ft_user_stopword_table=NULL;DROP TABLE probe.docs;DROP TABLE probe.words;"
                     f"CREATE TABLE probe.words(value VARCHAR(30) COLLATE {source});"
                     f"INSERT INTO probe.words VALUES('{word}');SET SESSION innodb_ft_user_stopword_table='probe/words';"
                     "CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_0900_ai_ci,FULLTEXT ft(body) WITH PARSER ngram);"
                     "INSERT INTO probe.docs VALUES(1,'ab'),(2,'Ab'),(3,'AB'),(4,'áb'),(5,'ss'),(6,'ßx'),(7,'zz')")
        expect(f"ngram source {source}, word {word!r}",
               matching(client, "ab Ab AB áb ss ßx zz", mode="IN NATURAL LANGUAGE MODE"), expected)


def source_access(client):
    client.query("CREATE TABLE probe.private_words(value VARCHAR(30));INSERT INTO probe.private_words VALUES('orchard');"
                 "CREATE USER 'stopword_reader'@'localhost';CREATE DATABASE own_db;"
                 "GRANT ALL ON own_db.* TO 'stopword_reader'@'localhost'")
    sql = ("SET SESSION innodb_ft_user_stopword_table='probe/private_words';"
           "CREATE TABLE own_db.docs(body TEXT,FULLTEXT ft(body));"
           "INSERT INTO own_db.docs VALUES('orchard'),('cobalt');"
           "SELECT COUNT(*) FROM own_db.docs WHERE MATCH(body) AGAINST('orchard')")
    result = subprocess.run(["mysql", "--no-defaults", *connection_options(client), "-ustopword_reader",
                             "--batch", "--skip-column-names", "-e", sql],
                            capture_output=True, text=True, check=True)
    expect("session source does not require SELECT privilege on the source", result.stdout.strip(), "0")


def verify(client, other):
    validation(client, other)
    precedence(client)
    capture_and_rebuild(client)
    collation_and_contents(client)
    phrases(client)
    ngram_source_collation(client)
    source_access(client)


def restart_lifetime():
    with tempfile.TemporaryDirectory(prefix="fsdb-custom-stopword-restart-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        log_path = root / "server.log"
        try:
            with log_path.open("w") as log:
                subprocess.run(["mysqld", "--no-defaults", "--initialize-insecure",
                                "--datadir=" + str(data), "--innodb-redo-log-capacity=64M"],
                               stdout=log, stderr=subprocess.STDOUT, check=True)
                for stage in ["initial", "changed", "dropped"]:
                    with startup["server"](data, str(root / "mysql.sock"), log, "ON") as client:
                        if stage == "initial":
                            client.query("CREATE DATABASE probe;CREATE TABLE probe.words(value VARCHAR(30));"
                                         "INSERT INTO probe.words VALUES('orchard');"
                                         "SET SESSION innodb_ft_user_stopword_table='probe/words'")
                            seed(client)
                            for table in ["cold_docs", "warm_docs"]:
                                client.query("CREATE TABLE probe." + table +
                                             "(id INT PRIMARY KEY,body TEXT,other TEXT,FULLTEXT ft(body));"
                                             "INSERT INTO probe." + table +
                                             " VALUES(1,'orchard','orchard'),(2,'cobalt','cobalt'),(3,'the','the')")
                        word_matches(client, "restart " + stage,
                                     ["5", "2", "3,5"] if stage == "dropped" else ["NULL", "2", "3"])
                        if stage == "initial":
                            client.query("DELETE FROM probe.words;INSERT INTO probe.words VALUES('cobalt')")
                        elif stage == "changed":
                            expect("source setting resets but index remembers source", client.query(
                                "SELECT @@SESSION.innodb_ft_user_stopword_table,@@GLOBAL.innodb_ft_server_stopword_table"), "NULL\tNULL")
                            for table in ["cold_docs", "warm_docs"]:
                                if table == "warm_docs":
                                    client.query("SELECT COUNT(*) FROM probe.warm_docs WHERE MATCH(body) AGAINST('orchard')")
                                client.query("ALTER TABLE probe." + table + " ADD FULLTEXT ft_other(other)")
                                for term, expected in [("orchard", "1"), ("cobalt", "2" if table == "cold_docs" else "NULL"), ("the", "3")]:
                                    expect(table + " added index " + term, client.query(
                                        "SELECT GROUP_CONCAT(id ORDER BY id) FROM probe." + table +
                                        " WHERE MATCH(other) AGAINST('" + term + "')"), expected)
                            client.query("INSERT INTO probe.docs VALUES(5,'orchard cobalt the')")
                            word_matches(client, "new writes use reloaded list; old postings remain", ["5", "2", "3,5"])
                            client.query("DROP TABLE probe.words")
                        else:
                            client.query("INSERT INTO probe.docs VALUES(6,'orchard cobalt the')")
                            word_matches(client, "missing source reloads builtin for new writes", ["5,6", "2,6", "3,5"])
                            client.query("CREATE TABLE probe.words(value VARCHAR(30));"
                                         "SET SESSION innodb_ft_user_stopword_table='probe/words';DROP TABLE probe.words;"
                                         "CREATE TABLE probe.missing_docs(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body));"
                                         "INSERT INTO probe.missing_docs VALUES(1,'orchard the');"
                                         "CREATE TABLE probe.words(value VARCHAR(30));INSERT INTO probe.words VALUES('orchard')")
                with startup["server"](data, str(root / "mysql.sock"), log, "ON") as client:
                    client.query("INSERT INTO probe.missing_docs VALUES(2,'orchard the')")
                    expect("missing source at creation is not remembered", matching(client, "orchard", table="missing_docs"), "1,2")
                    expect("missing source at creation retains builtin", matching(client, "the", table="missing_docs"), "NULL")

        except Exception:
            print(log_path.read_text(), flush=True)
            raise


if __name__ == "__main__":
    native["run"](verify)
    restart_lifetime()
    print("Custom stopword native oracle passed", flush=True)
