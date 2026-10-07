"""Verify native MySQL 8.4.11 COM_STMT_PREPARE source flags."""

import pathlib
import runpy
import socket
import struct

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))


def read_exact(connection, length):
    result = bytearray()
    while len(result) < length:
        chunk = connection.recv(length - len(result))
        if not chunk:
            raise RuntimeError("MySQL closed the metadata connection")
        result.extend(chunk)
    return bytes(result)


def read_packet(connection):
    header = read_exact(connection, 4)
    payload = read_exact(connection, int.from_bytes(header[:3], "little"))
    if payload[0] == 255:
        raise RuntimeError(f"MySQL error packet: {payload!r}")
    return payload


def send_packet(connection, payload, sequence=0):
    connection.sendall(len(payload).to_bytes(3, "little") + bytes([sequence]) + payload)


def column_flags(payload):
    offset = 0
    # All six identifying strings in this fixture fit the one-byte length form.
    for _ in range(6):
        length = payload[offset]
        if length >= 251:
            raise ValueError("Unexpected long column identifier in the oracle fixture")
        offset += length + 1
    if payload[offset] != 12:
        raise ValueError("Unexpected column-definition fixed field length")
    return int.from_bytes(payload[offset + 8:offset + 10], "little")


def verify(client, _writer):
    client.query("USE probe;CREATE TABLE t(id INT PRIMARY KEY AUTO_INCREMENT,"
                 "u INT NOT NULL UNIQUE,k INT NOT NULL,KEY(k));CREATE TABLE a(id INT PRIMARY KEY)")
    path = next(arg.split("=", 1)[1] for arg in client.process.args if arg.startswith("--socket="))
    with socket.socket(socket.AF_UNIX) as connection:
        connection.settimeout(10)
        connection.connect(path)
        read_packet(connection)
        # Protocol 4.1, secure auth and plugin auth; the disposable root has no password.
        handshake = struct.pack("<IIB", 0x88200, 16777216, 45) + bytes(23)
        send_packet(connection, handshake + b"root\0\0caching_sha2_password\0", sequence=1)
        if read_packet(connection)[0] != 0:
            raise RuntimeError("Unexpected native authentication response")
        for query, expected in [
            ("SELECT id,u,k FROM probe.t", [16899, 20485, 20489]),
            ("SELECT id AS renamed,u AS un,k AS kn FROM probe.t", [16899, 20485, 20489]),
            ("SELECT a.id,t.id,t.u,t.k FROM probe.a LEFT JOIN probe.t ON a.id=t.id", [20483, 16898, 20484, 20488]),
            ("SELECT id FROM (SELECT id FROM probe.t) d", [16899]),
            ("SELECT id FROM (SELECT DISTINCT id FROM probe.t) d", [1]),
            ("SELECT id FROM (SELECT id FROM probe.t GROUP BY id) d", [1]),
            ("SELECT id FROM (SELECT id FROM probe.t HAVING id>0) d", [1]),
            ("SELECT id FROM (SELECT id,ROW_NUMBER() OVER() AS rn FROM probe.t) d", [1]),
            ("SELECT id FROM (SELECT id,(SELECT 1) AS n FROM probe.t) d", [16899]),
            ("SELECT id FROM (SELECT id,(SELECT MAX(id) FROM probe.a) AS n FROM probe.t) d", [16899]),
            ("SELECT id FROM (SELECT id FROM probe.t LIMIT 1) d", [1]),
            ("WITH d AS (SELECT id,(SELECT 1) AS n FROM probe.t) SELECT id FROM d", [16899]),
            ("WITH d AS (SELECT id FROM probe.t LIMIT 1) SELECT id FROM d", [1]),
        ]:
            send_packet(connection, b"\x16" + query.encode())
            prepared = read_packet(connection)
            if prepared[0] != 0 or int.from_bytes(prepared[7:9], "little") != 0:
                raise RuntimeError("Unexpected PREPARE response for a parameter-free query")
            count = int.from_bytes(prepared[5:7], "little")
            actual = [column_flags(read_packet(connection)) for _ in range(count)]
            if count:
                if read_packet(connection)[0] != 254:
                    raise RuntimeError("Missing prepared-column EOF")
            oracle["expect"](query, actual, expected)
            send_packet(connection, b"\x19" + prepared[1:5])


if __name__ == "__main__":
    oracle["run"](verify)
