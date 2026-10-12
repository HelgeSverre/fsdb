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
      "scx=Latn", "i", 1555, "653fc207d45648866dcf78ce80dd2e30707ab5fad62a1ea695594de6c5426e36"
      "Cyrillic", "c", 508, "03188fa3fcd5b19593b01537c99f8450d60866f828457b64d27048badb55f791"
      "Cyrillic", "i", 508, "03188fa3fcd5b19593b01537c99f8450d60866f828457b64d27048badb55f791"
      "scx=Cyrl", "c", 521, "aee85ddae735d8f9c89079482e8bb7d67fd00d4d5be124b05653d4fb1e0e4403"
      "scx=Cyrl", "i", 521, "aee85ddae735d8f9c89079482e8bb7d67fd00d4d5be124b05653d4fb1e0e4403"
      "Arabic", "c", 1373, "f088b5daba99c5ab94d6fd287c3573936626ac92d50c975a53948c6dd13d552d"
      "Arabic", "i", 1373, "f088b5daba99c5ab94d6fd287c3573936626ac92d50c975a53948c6dd13d552d"
      "scx=Arab", "c", 1421, "aed5806cd5fb7202bae0be61c9805b55b1f0cf13972de66f1d46fa4eb5f5d609"
      "scx=Arab", "i", 1421, "aed5806cd5fb7202bae0be61c9805b55b1f0cf13972de66f1d46fa4eb5f5d609"
      "Devanagari", "c", 164, "03b3b629028fd89e3ba20610b2af2c2f284f4eafebd4ac00ba4d2c21f34d4bb8"
      "Devanagari", "i", 164, "03b3b629028fd89e3ba20610b2af2c2f284f4eafebd4ac00ba4d2c21f34d4bb8"
      "scx=Deva", "c", 221, "f7818e049964c2c64231dae10d176be1d0f69bca09a26bc27674b966fb03148f"
      "scx=Deva", "i", 221, "f7818e049964c2c64231dae10d176be1d0f69bca09a26bc27674b966fb03148f" ]

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
