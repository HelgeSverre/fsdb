#r "../../src/Fsdb/bin/Debug/net10.0/Fsdb.dll"

open System
open System.IO
open System.Security.Cryptography
open Fsdb

let expected =
    [ "c", 518, "fe9ff45d99489ce4b76ed0c253a3ccffba43805546a89873046d3845bbc853a0"
      "i", 520, "82287ee86552cbedfd2f2c835940cb24d64b0689e552d7c67e0b08cd13b81992" ]

for mode, expectedCount, expectedDigest in expected do
    let pattern =
        match Regexp.compile Collation.defaultCollation (Some mode) "\\p{Greek}" with
        | Ok pattern -> pattern
        | Error error -> failwithf "%A" error

    use bytes = new MemoryStream()
    let mutable count = 0
    for codePoint in 0 .. 0x10FFFF do
        if codePoint < 0xD800 || codePoint > 0xDFFF then
            if pattern.IsMatch(Char.ConvertFromUtf32 codePoint) then
                count <- count + 1
                bytes.Write(BitConverter.GetBytes codePoint, 0, 4)

    let digest = Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant()
    if count <> expectedCount || digest <> expectedDigest then
        failwithf "Greek script %s: expected %d/%s, got %d/%s" mode expectedCount expectedDigest count digest
    printfn "Greek script %s: %d matching scalars, digest %s" mode count digest
