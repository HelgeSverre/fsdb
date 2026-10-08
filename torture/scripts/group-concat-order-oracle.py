"""Pin GROUP_CONCAT duplicate retention, typed ordering, and ties on MySQL 8.4.11."""

import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
fixtures = [
    ("CREATE TABLE concat_order(id INT PRIMARY KEY,k INT,v VARCHAR(8) COLLATE utf8mb4_0900_ai_ci);INSERT INTO concat_order VALUES(1,2,'b'),(2,1,'a'),(3,2,'c'),(4,1,'B'),(5,2,'d'),(6,1,'e'),(7,2,'f'),(8,1,'A')", [
        ('SELECT GROUP_CONCAT(id ORDER BY k) AS s FROM concat_order', 's\n8,6,4,2,7,5,3,1\n'),
        ('SELECT GROUP_CONCAT(id ORDER BY k DESC) AS s FROM concat_order', 's\n7,5,3,1,8,6,4,2\n'),
        ('SELECT GROUP_CONCAT(id ORDER BY k,id) AS s FROM concat_order', 's\n2,4,6,8,1,3,5,7\n'),
        ('SELECT GROUP_CONCAT(id ORDER BY k,id DESC) AS s FROM concat_order', 's\n8,6,4,2,7,5,3,1\n'),
        ('SELECT GROUP_CONCAT(id ORDER BY k-k) AS s FROM concat_order', 's\n8,7,6,5,4,3,2,1\n'),
        ('SELECT GROUP_CONCAT(v) AS s FROM concat_order', 's\nb,a,c,B,d,e,f,A\n'),
        ('SELECT GROUP_CONCAT(DISTINCT v) AS s FROM concat_order', 's\na,b,c,d,e,f\n'),
        ('SELECT GROUP_CONCAT(DISTINCT v ORDER BY k) AS s FROM concat_order', 's\ne,a,f,d,c,b\n'),
        ('SELECT GROUP_CONCAT(DISTINCT v ORDER BY k DESC) AS s FROM concat_order', 's\nf,d,c,b,e,a\n'),
        ('SELECT GROUP_CONCAT(v ORDER BY k) AS s FROM concat_order', 's\nA,e,B,a,f,d,c,b\n'),
        ('SELECT GROUP_CONCAT(DISTINCT v ORDER BY k-k) AS s FROM concat_order', 's\nf,e,d,c,a,b\n'),
        ('SELECT GROUP_CONCAT(DISTINCT id ORDER BY k-k) AS s FROM concat_order', 's\n8,7,6,5,4,3,2,1\n'),
    ]),
    ("CREATE TABLE concat_types(id INT,n INT,e ENUM('z','a','m'),v VARCHAR(8) COLLATE utf8mb4_bin);INSERT INTO concat_types VALUES(1,10,'m','b'),(2,2,'z','a'),(3,1,'a','B'),(4,2,'z',NULL),(5,NULL,NULL,'A')", [
        ('SELECT GROUP_CONCAT(DISTINCT n) AS s FROM concat_types', 's\n1,2,10\n'),
        ('SELECT GROUP_CONCAT(DISTINCT e) AS s FROM concat_types', 's\nz,a,m\n'),
        ('SELECT GROUP_CONCAT(DISTINCT e ORDER BY e) AS s FROM concat_types', 's\nz,a,m\n'),
        ('SELECT GROUP_CONCAT(DISTINCT v) AS s FROM concat_types', 's\nA,B,a,b\n'),
        ('SELECT GROUP_CONCAT(id ORDER BY v) AS s FROM concat_types', 's\n4,5,3,2,1\n'),
    ]),
]


def verify(client, _writer):
    for setup, cases in fixtures:
        arguments["verify_cases"](client, [(setup, "")])
        for query, expected in cases:
            prepared = "PREPARE concat_ordering FROM '" + query.replace("'", "''") + "';EXECUTE concat_ordering"
            arguments["verify_cases"](client, [(query, expected), (prepared, expected)])


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
