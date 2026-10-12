module internal Fsdb.RegexpScriptProperties

let private codeUnitAtom first last =
    if first = last then sprintf "\\u%04X" first
    else sprintf "[\\u%04X-\\u%04X]" first last

let private scalarRangesAtom ranges =
    let bmpRanges =
        ranges
        |> List.choose (fun (first, last) ->
            if first <= 0xFFFF then Some(first, min last 0xFFFF) else None)

    let bmpAtom =
        bmpRanges
        |> List.map (fun (first, last) ->
            if first = last then sprintf "\\u%04X" first
            else sprintf "\\u%04X-\\u%04X" first last)
        |> String.concat ""
        |> fun members -> if members = "" then [] else [ "[" + members + "]" ]

    let pairAtom firstHigh lastHigh firstLow lastLow =
        codeUnitAtom firstHigh lastHigh + codeUnitAtom firstLow lastLow

    let supplementaryAtoms =
        ranges
        |> List.collect (fun (first, last) ->
            if last < 0x10000 then []
            else
                let highAndLow scalar =
                    let offset = scalar - 0x10000
                    0xD800 + offset / 0x400, 0xDC00 + offset % 0x400

                let firstHigh, firstLow = highAndLow (max first 0x10000)
                let lastHigh, lastLow = highAndLow last

                if firstHigh = lastHigh then
                    [ pairAtom firstHigh lastHigh firstLow lastLow ]
                else
                    [ pairAtom firstHigh firstHigh firstLow 0xDFFF
                      if lastHigh > firstHigh + 1 then
                          pairAtom (firstHigh + 1) (lastHigh - 1) 0xDC00 0xDFFF
                      pairAtom lastHigh lastHigh 0xDC00 lastLow ])

    "(?:" + String.concat "|" (bmpAtom @ supplementaryAtoms) + ")"

// ICU's Greek script includes Greek Extended and supplementary symbols, but
// excludes Coptic letters within the Greek and Coptic block.
let private greekScriptRanges =
    [ 0x0370, 0x0373; 0x0375, 0x0377; 0x037A, 0x037D; 0x037F, 0x037F
      0x0384, 0x0384; 0x0386, 0x0386; 0x0388, 0x038A; 0x038C, 0x038C
      0x038E, 0x03A1; 0x03A3, 0x03E1; 0x03F0, 0x03FF
      0x1D26, 0x1D2A; 0x1D5D, 0x1D61; 0x1D66, 0x1D6A; 0x1DBF, 0x1DBF
      0x1F00, 0x1F15; 0x1F18, 0x1F1D; 0x1F20, 0x1F45; 0x1F48, 0x1F4D
      0x1F50, 0x1F57; 0x1F59, 0x1F59; 0x1F5B, 0x1F5B; 0x1F5D, 0x1F5D
      0x1F5F, 0x1F7D; 0x1F80, 0x1FB4; 0x1FB6, 0x1FC4; 0x1FC6, 0x1FD3
      0x1FD6, 0x1FDB; 0x1FDD, 0x1FEF; 0x1FF2, 0x1FF4; 0x1FF6, 0x1FFE
      0x2126, 0x2126; 0xAB65, 0xAB65
      0x10140, 0x1018E; 0x101A0, 0x101A0; 0x1D200, 0x1D245 ]

let private greekScriptAtom =
    scalarRangesAtom greekScriptRanges

let private greekScriptFoldedAtom = "(?:" + greekScriptAtom + "|[\\u00B5\\u0345])"

let private greekScriptAtomFor ignoreCase =
    if ignoreCase then greekScriptFoldedAtom else greekScriptAtom

let private greekScriptExtensionMembers =
    "[\\u00B7\\u0300-\\u0301\\u0304\\u0306\\u0308\\u0313\\u0342\\u0345\\u0374\\u1DC0-\\u1DC1\\u205D]"

let private greekScriptExtensionsAtom ignoreCase =
    "(?:" + greekScriptAtomFor ignoreCase + "|" + greekScriptExtensionMembers + ")"

let private greekScriptAliases =
    set [ "GREEK"; "GREK"; "ISGREEK"; "SC=GREEK"; "SC=GREK"; "SCRIPT=GREEK"; "SCRIPT=GREK" ]

let private greekScriptExtensionAliases =
    set [ "SCX=GREEK"; "SCX=GREK"; "SCRIPT_EXTENSIONS=GREEK"; "SCRIPT_EXTENSIONS=GREK" ]

let private hanScriptRanges =
    [ 0x2E80, 0x2E99; 0x2E9B, 0x2EF3; 0x2F00, 0x2FD5
      0x3005, 0x3005; 0x3007, 0x3007; 0x3021, 0x3029; 0x3038, 0x303B
      0x3400, 0x4DBF; 0x4E00, 0x9FFF; 0xF900, 0xFA6D; 0xFA70, 0xFAD9
      0x16FE2, 0x16FE3; 0x16FF0, 0x16FF1
      0x20000, 0x2A6DF; 0x2A700, 0x2B739; 0x2B740, 0x2B81D
      0x2B820, 0x2CEA1; 0x2CEB0, 0x2EBE0; 0x2EBF0, 0x2EE5D
      0x2F800, 0x2FA1D; 0x30000, 0x3134A; 0x31350, 0x323AF ]

let private hanScriptExtensionMembers =
    [ 0x00B7, 0x00B7; 0x2FF0, 0x2FFF
      0x3001, 0x3003; 0x3006, 0x3006; 0x3008, 0x3011; 0x3013, 0x301F
      0x302A, 0x302D; 0x3030, 0x3030; 0x3037, 0x3037; 0x303C, 0x303F
      0x30FB, 0x30FB; 0x3190, 0x319F; 0x31C0, 0x31E5; 0x31EF, 0x31EF
      0x3220, 0x3247; 0x3280, 0x32B0; 0x32C0, 0x32CB; 0x32FF, 0x32FF
      0x3358, 0x3370; 0x337B, 0x337F; 0x33E0, 0x33FE
      0xA700, 0xA707; 0xFE45, 0xFE46; 0xFF61, 0xFF65
      0x1D360, 0x1D371; 0x1F250, 0x1F251 ]

let private hanScriptAtom = scalarRangesAtom hanScriptRanges
let private hanScriptExtensionsAtom = "(?:" + hanScriptAtom + "|" + scalarRangesAtom hanScriptExtensionMembers + ")"

let private hanScriptAliases =
    set [ "HAN"; "HANI"; "ISHAN"; "SC=HAN"; "SC=HANI"; "SCRIPT=HAN"; "SCRIPT=HANI" ]

let private hanScriptExtensionAliases =
    set [ "SCX=HAN"; "SCX=HANI"; "SCRIPT_EXTENSIONS=HAN"; "SCRIPT_EXTENSIONS=HANI" ]

let private latinScriptRanges =
    [ 0x0041, 0x005A; 0x0061, 0x007A; 0x00AA, 0x00AA; 0x00BA, 0x00BA
      0x00C0, 0x00D6; 0x00D8, 0x00F6; 0x00F8, 0x02B8; 0x02E0, 0x02E4
      0x1D00, 0x1D25; 0x1D2C, 0x1D5C; 0x1D62, 0x1D65
      0x1D6B, 0x1D77; 0x1D79, 0x1DBE; 0x1E00, 0x1EFF
      0x2071, 0x2071; 0x207F, 0x207F; 0x2090, 0x209C
      0x212A, 0x212B; 0x2132, 0x2132; 0x214E, 0x214E; 0x2160, 0x2188
      0x2C60, 0x2C7F; 0xA722, 0xA787; 0xA78B, 0xA7CD
      0xA7D0, 0xA7D1; 0xA7D3, 0xA7D3; 0xA7D5, 0xA7DC; 0xA7F2, 0xA7FF
      0xAB30, 0xAB5A; 0xAB5C, 0xAB64; 0xAB66, 0xAB69
      0xFB00, 0xFB06; 0xFF21, 0xFF3A; 0xFF41, 0xFF5A
      0x10780, 0x10785; 0x10787, 0x107B0; 0x107B2, 0x107BA
      0x1DF00, 0x1DF1E; 0x1DF25, 0x1DF2A ]

let private latinScriptExtensionMembers =
    [ 0x00B7, 0x00B7; 0x02BC, 0x02BC; 0x02C7, 0x02C7
      0x02C9, 0x02CB; 0x02CD, 0x02CD; 0x02D7, 0x02D7; 0x02D9, 0x02D9
      0x0300, 0x030E; 0x0310, 0x0311; 0x0313, 0x0313; 0x0320, 0x0320
      0x0323, 0x0325; 0x032D, 0x032E; 0x0330, 0x0331; 0x0358, 0x0358
      0x035E, 0x035E; 0x0363, 0x036F; 0x0485, 0x0486; 0x0951, 0x0952
      0x10FB, 0x10FB; 0x1DF8, 0x1DF8; 0x202F, 0x202F; 0x20F0, 0x20F0
      0x2E17, 0x2E17; 0xA700, 0xA707; 0xA92E, 0xA92E ]

let private latinScriptAtom = scalarRangesAtom latinScriptRanges
let private latinScriptExtensionsAtom = "(?:" + latinScriptAtom + "|" + scalarRangesAtom latinScriptExtensionMembers + ")"

let private latinScriptAliases =
    set [ "LATIN"; "LATN"; "ISLATIN"; "SC=LATIN"; "SC=LATN"; "SCRIPT=LATIN"; "SCRIPT=LATN" ]

let private latinScriptExtensionAliases =
    set [ "SCX=LATIN"; "SCX=LATN"; "SCRIPT_EXTENSIONS=LATIN"; "SCRIPT_EXTENSIONS=LATN" ]

let private cyrillicScriptRanges =
    [ 0x0400, 0x0484; 0x0487, 0x052F; 0x1C80, 0x1C8A
      0x1D2B, 0x1D2B; 0x1D78, 0x1D78; 0x2DE0, 0x2DFF
      0xA640, 0xA69F; 0xFE2E, 0xFE2F
      0x1E030, 0x1E06D; 0x1E08F, 0x1E08F ]

let private cyrillicScriptExtensionMembers =
    [ 0x02BC, 0x02BC; 0x0300, 0x0302; 0x0304, 0x0304
      0x0306, 0x0306; 0x0308, 0x0308; 0x030B, 0x030B
      0x0311, 0x0311; 0x0485, 0x0486; 0x1DF8, 0x1DF8
      0x2E43, 0x2E43 ]

let private cyrillicScriptAtom = scalarRangesAtom cyrillicScriptRanges
let private cyrillicScriptExtensionsAtom = "(?:" + cyrillicScriptAtom + "|" + scalarRangesAtom cyrillicScriptExtensionMembers + ")"

let private cyrillicScriptAliases =
    set [ "CYRILLIC"; "CYRL"; "ISCYRILLIC"; "SC=CYRILLIC"; "SC=CYRL"; "SCRIPT=CYRILLIC"; "SCRIPT=CYRL" ]

let private cyrillicScriptExtensionAliases =
    set [ "SCX=CYRILLIC"; "SCX=CYRL"; "SCRIPT_EXTENSIONS=CYRILLIC"; "SCRIPT_EXTENSIONS=CYRL" ]

let private scriptAtoms =
    [ greekScriptAliases, greekScriptAtom, greekScriptFoldedAtom
      greekScriptExtensionAliases, greekScriptExtensionsAtom false, greekScriptExtensionsAtom true
      hanScriptAliases, hanScriptAtom, hanScriptAtom
      hanScriptExtensionAliases, hanScriptExtensionsAtom, hanScriptExtensionsAtom
      latinScriptAliases, latinScriptAtom, latinScriptAtom
      latinScriptExtensionAliases, latinScriptExtensionsAtom, latinScriptExtensionsAtom
      cyrillicScriptAliases, cyrillicScriptAtom, cyrillicScriptAtom
      cyrillicScriptExtensionAliases, cyrillicScriptExtensionsAtom, cyrillicScriptExtensionsAtom ]
    |> List.collect (fun (aliases, sensitive, insensitive) ->
        aliases |> Set.toList |> List.map (fun name -> name, (sensitive, insensitive)))
    |> Map.ofList

let tryFindAtom ignoreCase name =
    Map.tryFind name scriptAtoms
    |> Option.map (fun (sensitive, insensitive) -> if ignoreCase then insensitive else sensitive)
