#r "../../src/Fsdb/bin/Debug/net10.0/Fsdb.dll"

open System
open System.IO
open System.Security.Cryptography
open Fsdb

let expected =
    [ "Greek", "c", 518, "fe9ff45d99489ce4b76ed0c253a3ccffba43805546a89873046d3845bbc853a0"
      "Greek", "i", 520, "82287ee86552cbedfd2f2c835940cb24d64b0689e552d7c67e0b08cd13b81992"
      "scx=Grek", "c", 531, "f1470937ece85b43a8e303fc71222a830754d6c0d9c9335c72906f793dd29e56"
      "scx=Grek", "i", 532, "32befcff5012c57221a15aa5c24bcd98b177bd452b6273cf1db3e50ff43a91f1"
      "Variation_Selector", "c", 260, "c539c99975ea71b6bc652c0d4bc1327c0e68e46ca369951847b0c2fef1aa5b77"
      "Variation_Selector", "i", 260, "c539c99975ea71b6bc652c0d4bc1327c0e68e46ca369951847b0c2fef1aa5b77"
      "Han", "c", 99030, "4d88d555fa81ea31035361dab2fa6c61d20de0fb7f044f7f2decec7b306c5f8c"
      "Han", "i", 99030, "4d88d555fa81ea31035361dab2fa6c61d20de0fb7f044f7f2decec7b306c5f8c"
      "scx=Hani", "c", 99338, "c2244d16981124663e29f8a1c552acd9a8f057b5a5ac718d1208033a1dfc8574"
      "scx=Hani", "i", 99338, "c2244d16981124663e29f8a1c552acd9a8f057b5a5ac718d1208033a1dfc8574"
      "Latin", "c", 1487, "e6fc4143c01663dd2ac0c1c315be88b426374cdc9090b2135c68a638dc2a64a9"
      "Latin", "i", 1487, "e6fc4143c01663dd2ac0c1c315be88b426374cdc9090b2135c68a638dc2a64a9"
      "scx=Latn", "c", 1555, "653fc207d45648866dcf78ce80dd2e30707ab5fad62a1ea695594de6c5426e36"
      "scx=Latn", "i", 1555, "653fc207d45648866dcf78ce80dd2e30707ab5fad62a1ea695594de6c5426e36" ]

for propertyName, mode, expectedCount, expectedDigest in expected do
    let pattern =
        match Regexp.compile Collation.defaultCollation (Some mode) ("\\p{" + propertyName + "}") with
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
        failwithf "%s %s: expected %d/%s, got %d/%s" propertyName mode expectedCount expectedDigest count digest
    printfn "%s %s: %d matching scalars, digest %s" propertyName mode count digest
