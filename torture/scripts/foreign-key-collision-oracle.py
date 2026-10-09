import json, re, runpy, subprocess
from pathlib import Path
root = Path(__file__).resolve().parents[2]
oracle = runpy.run_path(str(root / 'torture/scripts/fulltext-transaction-oracle.py'))
reset = ['USE probe', 'SET foreign_key_checks=0', 'DROP TABLE IF EXISTS child,parent,other,renamed', 'SET foreign_key_checks=1', 'CREATE TABLE parent(n INT PRIMARY KEY)']
observe = ["SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME"]
existing = 'CREATE TABLE child(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))'
cases = []
def add(name, statements):
    cases.append(dict(name=name, statements=reset + statements + observe))
for name, constraint in [('schema', 'shared'), ('case-insensitive', 'SHARED')]:
    add(name, [existing, f'CREATE TABLE other(a INT,CONSTRAINT {constraint} FOREIGN KEY(a) REFERENCES parent(n))'])
add('checks-disabled', [existing, 'SET foreign_key_checks=0', 'CREATE TABLE other(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))', 'SET foreign_key_checks=1'])
add('generated-explicit', ['CREATE TABLE child(a INT,b INT,FOREIGN KEY(a) REFERENCES parent(n),CONSTRAINT child_ibfk_1 FOREIGN KEY(b) REFERENCES parent(n))'])
add('duplicate-explicit-shared-index', ['CREATE TABLE child(a INT,KEY a(a),CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n),CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))'])
add('alter-existing', [existing, 'ALTER TABLE child ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n)'])
add('alter-other', [existing, 'CREATE TABLE other(a INT)', 'ALTER TABLE other ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n)'])
add('alter-drop-add', [existing, 'ALTER TABLE child DROP FOREIGN KEY shared,ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n)'])
add('alter-add-drop', [existing, 'ALTER TABLE child ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n),DROP FOREIGN KEY shared'])
add('alter-two-adds', ['CREATE TABLE child(a INT,b INT)', 'ALTER TABLE child ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n),ADD CONSTRAINT shared FOREIGN KEY(b) REFERENCES parent(n)'])
add('missing-parent-precedence', [existing, 'CREATE TABLE other(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES missing(n))'])
def probe(client, _):
    results = []
    for case in cases:
        result = subprocess.run([*client.process.args, '--column-names', '--force'], input=';\n'.join(case['statements'])+';\n', capture_output=True, text=True, timeout=30)
        client.query('SELECT 1')
        results.append(dict(**case, stdout=result.stdout, stderr=result.stderr, errors=[[int(code), state] for code,state in re.findall(r'ERROR (\d+) \((\w+)\)', result.stderr)]))
        print(case['name'], result.stderr.strip(), flush=True)
    Path('/tmp/fsdb-fk-collision-native.json').write_text(json.dumps(results, indent=2))
if __name__ == '__main__':
    oracle['run'](probe)
