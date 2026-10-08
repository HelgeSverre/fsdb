"""Pin joins beyond fsdb's candidate ceiling against native MySQL 8.4.11."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
values = ",".join(f"({number})" for number in range(1001))
cases = [
    ("DROP TABLE IF EXISTS cap_a,cap_b;CREATE TABLE cap_a(n INT);CREATE TABLE cap_b(n INT);"
     f"INSERT INTO cap_a VALUES{values};INSERT INTO cap_b SELECT * FROM cap_a", ""),
    ("SELECT COUNT(*) AS n FROM cap_a a JOIN cap_b b ON a.n+b.n<0", "n\n0\n"),
    ("SELECT COUNT(*) AS n FROM cap_a a LEFT JOIN cap_b b ON a.n+b.n<0", "n\n1001\n"),
    ("SELECT COUNT(*) AS n FROM cap_a a RIGHT JOIN cap_b b ON a.n+b.n<0", "n\n1001\n"),
    ("CREATE TABLE cap_numbers(n INT);INSERT INTO cap_numbers VALUES" + ",".join(f"({number})" for number in range(1,1002)), ""),
    ("SELECT 1 AS n FROM cap_numbers a JOIN cap_numbers b ON a.n+b.n>0 LIMIT 1", "n\n1\n"),
]

for indexed in (False, True):
    cases.append(("DROP TABLE IF EXISTS cap_keys;CREATE TABLE cap_keys(n INT,k INT" +
                  (",KEY(k)" if indexed else "") +
                  ");INSERT INTO cap_keys SELECT n,1 FROM cap_a", ""))
    for kind, count in (("JOIN", 0), ("LEFT JOIN", 1001), ("RIGHT JOIN", 1001)):
        cases.append((f"SELECT COUNT(*) AS n FROM cap_keys a {kind} cap_keys b ON a.k=b.k AND a.n+b.n<0", f"n\n{count}\n"))
    cases.append(("SELECT 1 AS n FROM cap_keys a JOIN cap_keys b ON a.k=b.k AND a.n+b.n<0 LIMIT 1", ""))

def verify(client, _writer):
    arguments["verify_cases"](client, cases)

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
