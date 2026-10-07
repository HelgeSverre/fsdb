"""Verify stopword configuration lifetime on disposable native MySQL 8.4.11."""

import pathlib
import runpy


native = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))
expect = native["expect"]
matches = native["matches"]
setup = native["setup"]


def verify(client, _other):
    expect("default stopword settings", client.query(
        "SELECT @@GLOBAL.innodb_ft_enable_stopword,@@SESSION.innodb_ft_enable_stopword"), "1\t1")
    for parser, term in [("", "the"), (" WITH PARSER ngram", "ab")]:
        for enabled in ["ON", "OFF"]:
            initial = "NULL" if enabled == "ON" else "4"
            opposite = "OFF" if enabled == "ON" else "ON"
            replacement = "4" if enabled == "ON" else "NULL"

            def seed():
                client.query("SET SESSION innodb_ft_enable_stopword=" + enabled)
                setup(client, parser=parser)
                client.query(f"INSERT INTO probe.docs VALUES(4,'{term} orchard')")
                client.query("SET SESSION innodb_ft_enable_stopword=" + opposite)

            seed()
            expect(f"{parser} {enabled} query setting", client.query(matches(term)), initial)
            for mode, expected in [
                ("IN NATURAL LANGUAGE MODE", initial),
                ("WITH QUERY EXPANSION", "NULL" if enabled == "ON" else "1,2,4"),
            ]:
                statement = ("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs "
                             f"WHERE MATCH(body) AGAINST('{term}' {mode})")
                expect(f"{parser} {enabled} {mode}", client.query(statement), expected)

            client.query(f"INSERT INTO probe.docs VALUES(5,'{term} cobalt')")
            expect("later insert retains policy", client.query(matches(term)),
                   "NULL" if enabled == "ON" else "4,5")
            client.query("ALTER TABLE probe.docs ADD COLUMN other TEXT;UPDATE probe.docs SET other=body;"
                         f"ALTER TABLE probe.docs ADD FULLTEXT KEY other_ft(other){parser}")
            expect("second index adopts current policy", client.query(matches(term, "other")),
                   "4,5" if enabled == "ON" else "NULL")
            expect("adding an index also refreshes the existing index", client.query(matches(term)),
                   "4,5" if enabled == "ON" else "NULL")

            for label, ddl, expected in [
                ("combined drop/add", f"ALTER TABLE probe.docs DROP INDEX ft,ADD FULLTEXT KEY ft(body){parser}", initial),
                ("separate drop/add", f"ALTER TABLE probe.docs DROP INDEX ft;ALTER TABLE probe.docs ADD FULLTEXT KEY ft(body){parser}", replacement),
                ("engine rebuild", "ALTER TABLE probe.docs ENGINE=InnoDB", replacement),
            ]:
                seed()
                client.query(ddl)
                expect(f"{parser} {enabled} {label}", client.query(matches(term)), expected)
    client.query("SET SESSION innodb_ft_enable_stopword=ON")


if __name__ == "__main__":
    native["run"](verify)
