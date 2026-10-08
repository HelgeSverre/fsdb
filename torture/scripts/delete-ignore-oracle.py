"""Pin DELETE IGNORE foreign-key, trigger, LIMIT, and rollback behavior."""
import pathlib
import re
import runpy
import subprocess

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
setup = "USE probe;DROP TABLE IF EXISTS child;DROP TABLE IF EXISTS parent;CREATE TABLE parent(id INT PRIMARY KEY);CREATE TABLE child(id INT PRIMARY KEY,pid INT,CONSTRAINT fk_parent FOREIGN KEY(pid) REFERENCES parent(id));INSERT INTO parent VALUES(1),(2),(3);INSERT INTO child VALUES(1,2);"
cases = [('DELETE IGNORE FROM parent ORDER BY id;SHOW WARNINGS;SELECT id FROM parent ORDER BY id;SELECT '
  'id,pid FROM child ORDER BY id',
  'Level\tCode\tMessage\n'
  'Warning\t1451\tCannot delete or update a parent row: a foreign key constraint fails '
  '(`probe`.`child`, CONSTRAINT `fk_parent` FOREIGN KEY (`pid`) REFERENCES `parent` (`id`))\n'
  'id\n'
  '2\n'
  'id\tpid\n'
  '1\t2\n',
  []),
 ('DELETE IGNORE FROM parent ORDER BY id LIMIT 1;SHOW WARNINGS;SELECT id FROM parent ORDER BY '
  'id;SELECT id,pid FROM child ORDER BY id',
  'id\n2\n3\nid\tpid\n1\t2\n',
  []),
 ('DELETE IGNORE FROM parent ORDER BY id DESC LIMIT 2;SHOW WARNINGS;SELECT id FROM parent ORDER BY '
  'id;SELECT id,pid FROM child ORDER BY id',
  'Level\tCode\tMessage\n'
  'Warning\t1451\tCannot delete or update a parent row: a foreign key constraint fails '
  '(`probe`.`child`, CONSTRAINT `fk_parent` FOREIGN KEY (`pid`) REFERENCES `parent` (`id`))\n'
  'id\n'
  '1\n'
  '2\n'
  'id\tpid\n'
  '1\t2\n',
  []),
 ('DELETE IGNORE FROM parent WHERE id=2;SHOW WARNINGS;SELECT id FROM parent ORDER BY id;SELECT '
  'id,pid FROM child ORDER BY id',
  'Level\tCode\tMessage\n'
  'Warning\t1451\tCannot delete or update a parent row: a foreign key constraint fails '
  '(`probe`.`child`, CONSTRAINT `fk_parent` FOREIGN KEY (`pid`) REFERENCES `parent` (`id`))\n'
  'id\n'
  '1\n'
  '2\n'
  '3\n'
  'id\tpid\n'
  '1\t2\n',
  []),
 ('DELETE IGNORE p FROM parent p LEFT JOIN child c ON c.pid=p.id;SHOW WARNINGS;SELECT id FROM '
  'parent ORDER BY id;SELECT id,pid FROM child ORDER BY id',
  'Level\tCode\tMessage\n'
  'Warning\t1451\tCannot delete or update a parent row: a foreign key constraint fails '
  '(`probe`.`child`, CONSTRAINT `fk_parent` FOREIGN KEY (`pid`) REFERENCES `parent` (`id`))\n'
  'id\n'
  '2\n'
  'id\tpid\n'
  '1\t2\n',
  []),
 ('DELETE IGNORE FROM p USING parent p LEFT JOIN child c ON c.pid=p.id;SHOW WARNINGS;SELECT id '
  'FROM parent ORDER BY id;SELECT id,pid FROM child ORDER BY id',
  'Level\tCode\tMessage\n'
  'Warning\t1451\tCannot delete or update a parent row: a foreign key constraint fails '
  '(`probe`.`child`, CONSTRAINT `fk_parent` FOREIGN KEY (`pid`) REFERENCES `parent` (`id`))\n'
  'id\n'
  '2\n'
  'id\tpid\n'
  '1\t2\n',
  []),
 ("CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW SIGNAL SQLSTATE '45000' SET "
  "MESSAGE_TEXT='blocked';DELETE IGNORE FROM parent WHERE id=1;SHOW WARNINGS;SELECT id FROM parent "
  'ORDER BY id;SELECT id,pid FROM child ORDER BY id',
  'Level\tCode\tMessage\nError\t1644\tblocked\nid\n1\n2\n3\nid\tpid\n1\t2\n',
  [(1644, '45000')]),
 ('ALTER TABLE child DROP FOREIGN KEY fk_parent;ALTER TABLE child ADD CONSTRAINT fk_parent FOREIGN '
  'KEY(pid) REFERENCES parent(id) ON DELETE CASCADE;DELETE IGNORE FROM parent;SHOW WARNINGS;SELECT '
  'id FROM parent ORDER BY id;SELECT id,pid FROM child ORDER BY id',
  '',
  []),
 ('BEGIN;DELETE IGNORE FROM parent ORDER BY id;SHOW WARNINGS;ROLLBACK;SHOW WARNINGS;SELECT id FROM '
  'parent ORDER BY id;SELECT id,pid FROM child ORDER BY id',
  'Level\tCode\tMessage\n'
  'Warning\t1451\tCannot delete or update a parent row: a foreign key constraint fails '
  '(`probe`.`child`, CONSTRAINT `fk_parent` FOREIGN KEY (`pid`) REFERENCES `parent` (`id`))\n'
  'id\n'
  '1\n'
  '2\n'
  '3\n'
  'id\tpid\n'
  '1\t2\n',
  [])]

def verify(client, _writer):
    for sql, expected, errors in cases:
        result = subprocess.run(
            [*client.process.args, "--column-names", "--force"],
            input=setup + sql + ";\n", capture_output=True, text=True, timeout=30,
        )
        assert result.returncode == 0, (sql, result.stderr)
        actual_errors = [(int(code), state) for code, state in re.findall(r"ERROR (\d+) \((\w+)\)", result.stderr)]
        assert actual_errors == errors, (sql, errors, result.stderr)
        arguments["oracle"]["expect"](sql, result.stdout, expected)

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
