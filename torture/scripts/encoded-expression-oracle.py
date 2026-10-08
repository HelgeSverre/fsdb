"""Pin encoded string expression behavior against native MySQL 8.4.11."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ("SELECT HEX(REVERSE(_ucs2 X'0041D83DDE000042')) AS h;SHOW WARNINGS", "h\n0042DE00D83D0041\nLevel\tCode\tMessage\nWarning\t1287\t'ucs2' is deprecated and will be removed in a future release. Please use utf8mb4 instead\n"),
    ("SELECT HEX(LEFT(_ucs2 X'0041D83DDE000042',2)) AS h;SHOW WARNINGS", "h\n0041D83D\nLevel\tCode\tMessage\nWarning\t1287\t'ucs2' is deprecated and will be removed in a future release. Please use utf8mb4 instead\n"),
    ("SELECT HEX(RIGHT(_ucs2 X'0041D83DDE000042',2)) AS h;SHOW WARNINGS", "h\nDE000042\nLevel\tCode\tMessage\nWarning\t1287\t'ucs2' is deprecated and will be removed in a future release. Please use utf8mb4 instead\n"),
    ("SELECT HEX(SUBSTRING(_ucs2 X'0041D83DDE000042',2,1)) AS h;SHOW WARNINGS", "h\nD83D\nLevel\tCode\tMessage\nWarning\t1287\t'ucs2' is deprecated and will be removed in a future release. Please use utf8mb4 instead\n"),
    ("SELECT HEX(CONCAT(_ucs2 X'0041D83DDE000042',_ucs2 X'0041D83DDE000042')) AS h;SHOW WARNINGS", "h\n0041D83DDE0000420041D83DDE000042\nLevel\tCode\tMessage\nWarning\t1287\t'ucs2' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nWarning\t1287\t'ucs2' is deprecated and will be removed in a future release. Please use utf8mb4 instead\n"),
    ("SELECT HEX(REVERSE(_ascii X'418042')) AS h;SHOW WARNINGS", "h\n428041\nLevel\tCode\tMessage\nWarning\t1300\tInvalid ascii character string: '8042'\n"),
    ("SELECT HEX(LEFT(_ascii X'418042',2)) AS h;SHOW WARNINGS", "h\n4180\nLevel\tCode\tMessage\nWarning\t1300\tInvalid ascii character string: '8042'\n"),
    ("SELECT HEX(RIGHT(_ascii X'418042',2)) AS h;SHOW WARNINGS", "h\n8042\nLevel\tCode\tMessage\nWarning\t1300\tInvalid ascii character string: '8042'\n"),
    ("SELECT HEX(SUBSTRING(_ascii X'418042',2,1)) AS h;SHOW WARNINGS", "h\n80\nLevel\tCode\tMessage\nWarning\t1300\tInvalid ascii character string: '8042'\n"),
    ("SELECT HEX(CONCAT(_ascii X'418042',_ascii X'418042')) AS h;SHOW WARNINGS", "h\n418042418042\nLevel\tCode\tMessage\nWarning\t1300\tInvalid ascii character string: '8042'\nWarning\t1300\tInvalid ascii character string: '8042'\n"),
    ("SELECT HEX(REVERSE(_utf8mb3'a😀b')) AS h;SHOW WARNINGS", "h\n6280989FF061\nLevel\tCode\tMessage\nWarning\t1287\t'utf8mb3' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nWarning\t1300\tInvalid utf8mb3 character string: 'F09F98'\n"),
    ("SELECT HEX(LEFT(_utf8mb3'a😀b',2)) AS h;SHOW WARNINGS", "h\n61F0\nLevel\tCode\tMessage\nWarning\t1287\t'utf8mb3' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nWarning\t1300\tInvalid utf8mb3 character string: 'F09F98'\n"),
    ("SELECT HEX(RIGHT(_utf8mb3'a😀b',2)) AS h;SHOW WARNINGS", "h\n8062\nLevel\tCode\tMessage\nWarning\t1287\t'utf8mb3' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nWarning\t1300\tInvalid utf8mb3 character string: 'F09F98'\n"),
    ("SELECT HEX(SUBSTRING(_utf8mb3'a😀b',2,1)) AS h;SHOW WARNINGS", "h\nF0\nLevel\tCode\tMessage\nWarning\t1287\t'utf8mb3' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nWarning\t1300\tInvalid utf8mb3 character string: 'F09F98'\n"),
    ("SELECT HEX(CONCAT(_utf8mb3'a😀b',_utf8mb3'a😀b')) AS h;SHOW WARNINGS", "h\n61F09F98806261F09F988062\nLevel\tCode\tMessage\nWarning\t1287\t'utf8mb3' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nWarning\t1287\t'utf8mb3' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nWarning\t1300\tInvalid utf8mb3 character string: 'F09F98'\nWarning\t1300\tInvalid utf8mb3 character string: 'F09F98'\n"),
]

def verify(client, _writer):
    arguments["verify_cases"](client, cases)

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
