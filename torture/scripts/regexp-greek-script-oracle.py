"""Check MySQL 8.4.11 Greek-script membership over every Unicode scalar."""

import hashlib
import pathlib
import runpy
import struct

native = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))

EXPECTED = {
    ("Greek", "c"): (518, "fe9ff45d99489ce4b76ed0c253a3ccffba43805546a89873046d3845bbc853a0"),
    ("Greek", "i"): (520, "82287ee86552cbedfd2f2c835940cb24d64b0689e552d7c67e0b08cd13b81992"),
    ("scx=Grek", "c"): (531, "f1470937ece85b43a8e303fc71222a830754d6c0d9c9335c72906f793dd29e56"),
    ("scx=Grek", "i"): (532, "32befcff5012c57221a15aa5c24bcd98b177bd452b6273cf1db3e50ff43a91f1"),
}


def members(client, property_name, mode):
    code_points = []
    for start in range(0, 0x110000, 4096):
        text = "".join(
            chr(code_point)
            for code_point in range(start, min(start + 4096, 0x110000))
            if not 0xD800 <= code_point <= 0xDFFF
        )
        if not text:
            continue
        query = (
            "SELECT HEX(REGEXP_REPLACE(CONVERT(0x"
            + text.encode("utf-8").hex()
            + " USING utf8mb4),'\\\\P{"
            + property_name
            + "}','',1,0,'"
            + mode
            + "'))"
        )
        matched = bytes.fromhex(client.query(query)).decode("utf-8")
        code_points.extend(map(ord, matched))
    return code_points


def verify(client, _):
    for (property_name, mode), (expected_count, expected_digest) in EXPECTED.items():
        code_points = members(client, property_name, mode)
        digest = hashlib.sha256(
            b"".join(struct.pack("<I", code_point) for code_point in code_points)
        ).hexdigest()
        native["expect"](f"{property_name} {mode} members", len(code_points), expected_count)
        native["expect"](f"{property_name} {mode} digest", digest, expected_digest)


if __name__ == "__main__":
    native["run"](verify)
