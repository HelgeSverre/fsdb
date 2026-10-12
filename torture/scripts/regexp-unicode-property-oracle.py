"""Check selected MySQL 8.4.11 Unicode properties over every scalar."""

import argparse
import hashlib
import pathlib
import runpy
import struct
import subprocess

native = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))

COMMON_EXPECTED = {
    ("Greek", "c"): (518, "fe9ff45d99489ce4b76ed0c253a3ccffba43805546a89873046d3845bbc853a0"),
    ("Greek", "i"): (520, "82287ee86552cbedfd2f2c835940cb24d64b0689e552d7c67e0b08cd13b81992"),
    ("scx=Grek", "c"): (531, "f1470937ece85b43a8e303fc71222a830754d6c0d9c9335c72906f793dd29e56"),
    ("scx=Grek", "i"): (532, "32befcff5012c57221a15aa5c24bcd98b177bd452b6273cf1db3e50ff43a91f1"),
    ("Variation_Selector", "c"): (260, "c539c99975ea71b6bc652c0d4bc1327c0e68e46ca369951847b0c2fef1aa5b77"),
    ("Variation_Selector", "i"): (260, "c539c99975ea71b6bc652c0d4bc1327c0e68e46ca369951847b0c2fef1aa5b77"),
}

PINNED_HAN = {
    ("Han", "c"): (99030, "4d88d555fa81ea31035361dab2fa6c61d20de0fb7f044f7f2decec7b306c5f8c"),
    ("Han", "i"): (99030, "4d88d555fa81ea31035361dab2fa6c61d20de0fb7f044f7f2decec7b306c5f8c"),
    ("scx=Hani", "c"): (99338, "c2244d16981124663e29f8a1c552acd9a8f057b5a5ac718d1208033a1dfc8574"),
    ("scx=Hani", "i"): (99338, "c2244d16981124663e29f8a1c552acd9a8f057b5a5ac718d1208033a1dfc8574"),
}

PINNED_IMAGE = "sha256:b3b90af2a6552ae30c266fdb7d5dd55f3afb72404bb78d37fe8a23eb857fd3fb"


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


def verify(client, expected):
    for (property_name, mode), (expected_count, expected_digest) in expected.items():
        code_points = members(client, property_name, mode)
        digest = hashlib.sha256(
            b"".join(struct.pack("<I", code_point) for code_point in code_points)
        ).hexdigest()
        native["expect"](f"{property_name} {mode} members", len(code_points), expected_count)
        native["expect"](f"{property_name} {mode} digest", digest, expected_digest)


def verify_pinned(container):
    image = subprocess.check_output(
        ["docker", "inspect", "--format", "{{.Image}}", container], text=True
    ).strip()
    native["expect"]("pinned MySQL image", image, PINNED_IMAGE)
    binding = subprocess.check_output(["docker", "port", container, "3306/tcp"], text=True).strip()
    port = binding.rsplit(":", 1)[1]
    client = native["Client"](["-h127.0.0.1", "-P" + port, "-ptorture-secret"])
    try:
        native["expect"]("version", client.query("SELECT VERSION()"), "8.4.11")
        verify(client, COMMON_EXPECTED | PINNED_HAN)
    finally:
        client.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pinned-container", help="running container from torture/compose.yaml")
    args = parser.parse_args()
    if args.pinned_container:
        verify_pinned(args.pinned_container)
    else:
        native["run"](lambda client, _: verify(client, COMMON_EXPECTED))
