/// String collation semantics — the single home for how fsdb compares and
/// sorts text. Everything that needs string semantics delegates here;
/// nothing re-derives its own rules per call site.
///
/// The utf8mb4 collation set MySQL 8.4 ships is registered below, using
/// ICU collation with explicit sensitivity and padding
/// policies. Host ICU and MySQL use different UCA/CLDR versions, so exact
/// weight strings and accent tie-break ordering may differ.
///  - `utf8mb4_unicode_520_ci`/legacy language collations use ICU's CLDR
///    tailoring rather than MySQL's UCA 5.2/4.0 weight tables.
///  - LIKE folds per character and never expands ('æ' LIKE 'ae' is false
///    while 'æ' = 'ae' is true — MySQL-verified) — see `Executor.likeMatch`.
///  - identifiers (column/table names) keep their ordinal comparison —
///    MySQL's identifier collation is a separate concern.
module Fsdb.Collation

open System
open System.Globalization
open System.Text

/// One collation = the behaviors the engine consumes. Everything else
/// delegates; nothing re-derives its own rules.
type Collation =
    { Name: string
      /// Full order for `ORDER BY`/`<`/`>`: primary, then secondary, then
      /// tertiary weights — accents and case break ties among
      /// primary-equal strings, exactly as MySQL's ai_ci sorts them.
      /// `_bin` collations order by bytes instead.
      Compare: string -> string -> int
      /// The *folded* order: accent/case-only differences compare equal
      /// (0). Drives `Value.compare`, so equality, hash-join keys, and
      /// unique lookups all inherit the collation's folding.
      ComparePrimary: string -> string -> int
      /// Equality + unique-key semantics: primary weights only for
      /// `_ai_ci` ('åge' = 'age'); trailing spaces ignored under PAD SPACE.
      Equals: string -> string -> bool
      /// A canonical index key: `Equals a b` iff `KeyOf a = KeyOf b` — the
      /// unique index and its point lookups both key on this.
      KeyOf: string -> string
      /// Sort weights without PAD SPACE normalization. `WEIGHT_STRING()`
      /// exposes these bytes, so trailing spaces remain observable.
      WeightOf: string -> byte[]
      /// A hash code consistent with `ComparePrimary`: strings `Equals`
      /// says are equal hash equal (the converse never needs to hold). The
      /// hash join buckets on this instead of `KeyOf` — a hash needs no
      /// canonical form, and `KeyOf` materializes a full sort key (plus a
      /// hex string) where a plain hash will do.
      HashOf: string -> int
      /// Per-character LIKE folding, explicitly without expansions.
      CharEquals: char -> char -> bool
      /// Prefix matching under the same case, accent, and locale rules as
      /// equality. Full-text wildcard terms use this because canonical sort
      /// keys do not preserve textual prefix boundaries.
      IsPrefix: string -> string -> bool
      /// PAD SPACE: trailing spaces are insignificant — `Equals` trims,
      /// and LIKE trims both subject and pattern ends before matching.
      PadSpace: bool }

// ---------------------------------------------------------------------------
// Construction from a small spec — every registered collation is one line
// of data below, nothing is hand-rolled.
// ---------------------------------------------------------------------------

let private icu = CultureInfo.InvariantCulture.CompareInfo

/// Tries to load a locale's collation; falls back to the invariant one
/// (and records the miss) for locales a host's ICU doesn't ship (e.g.
/// Esperanto on some builds) — documented approximation, never a crash.
let private compareInfoFor (locale: string option) : CompareInfo =
    match locale with
    | None -> icu
    | Some l ->
        try
            CultureInfo(l).CompareInfo
        with _ ->
            Log.diagnostic "fsdb: collation: locale '%s' unavailable on this ICU build, using invariant" l
            icu

type private Spec =
    { Locale: string option
      /// Equality folding: the CompareOptions applied to ComparePrimary/
      /// Equals/KeyOf/CharEquals (`_ai_ci` = IgnoreCase|IgnoreNonSpace,
      /// `_as_ci` = IgnoreCase, `_as_cs` = None).
      Fold: CompareOptions
      /// PAD SPACE (legacy + `_bin`): trailing spaces ignored in equality,
      /// and in sorting a trailing space sorts *before* end-of-string
      /// (MySQL-verified: ['a '] < ['a'] < ['ab']). NO PAD: significant.
      PadSpace: bool
      /// `_bin`: compare by charset-encoded bytes, not ICU weights.
      ByteOrder: bool }

let private countTrailingSpaces (s: string) : int =
    let mutable n = 0
    let mutable i = s.Length - 1

    while i >= 0 && s.[i] = ' ' do
        n <- n + 1
        i <- i - 1

    n

let private makeCollation (name: string) (spec: Spec) : Collation =
    let ci = compareInfoFor spec.Locale
    let trim (s: string) = if spec.PadSpace then s.TrimEnd(' ') else s
    let charset =
        match name.IndexOf '_' with
        | -1 -> name
        | index -> name[..index - 1]
        |> Charset.canonicalName

    let ordinalBytes (s: string) =
        s
        |> Seq.collect (fun value -> [ byte (int value >>> 8); byte value ])
        |> Array.ofSeq

    let binaryBytesWithoutPadding (s: string) =
        match Charset.tryEncodeStrict charset s with
        | Some bytes -> Array.append [| 0uy |] bytes
        | None -> Array.append [| 1uy |] (ordinalBytes s)

    let binaryBytes (s: string) = binaryBytesWithoutPadding (trim s)
    let compareBinary (a: string) (b: string) =
        (binaryBytes a).AsSpan().SequenceCompareTo((binaryBytes b).AsSpan())
    let binaryPrefix (value: string) (prefix: string) =
        match Charset.tryEncodeStrict charset value, Charset.tryEncodeStrict charset prefix with
        | Some value, Some prefix -> value.AsSpan().StartsWith(prefix.AsSpan())
        | _ -> value.StartsWith(prefix, StringComparison.Ordinal)

    let foldText (value: string) =
        if name = "utf8mb4_general_ci" then
            value.Replace("ß", "s").Replace("ẞ", "s")
        else
            value

    let primaryText (s: string) = trim s |> foldText

    let binaryWeight (s: string) =
        if name = "utf8mb3_bin" || name = "utf8mb4_bin" then
            s.EnumerateRunes()
            |> Seq.collect (fun rune ->
                let value = rune.Value
                [ byte (value >>> 16); byte (value >>> 8); byte value ])
            |> Array.ofSeq
        else
            Charset.tryEncodeStrict charset s |> Option.defaultWith (fun () -> binaryBytesWithoutPadding s)

    let compareFull (a: string) (b: string) : int =
        if String.Equals(a, b, StringComparison.Ordinal) then
            0
        elif spec.ByteOrder then
            let c = compareBinary a b
            // trimmed-equal under PAD SPACE: the extra-space side sorts
            // first (MySQL-verified), same tie-break as the ICU branch.
            if c <> 0 then c else countTrailingSpaces b - countTrailingSpaces a
        else
            let primary = ci.Compare(primaryText a, primaryText b, spec.Fold)

            if primary <> 0 then
                primary
            else
                let tieBreak = ci.Compare(trim a, trim b, CompareOptions.None)

                if tieBreak <> 0 then
                    tieBreak
                elif spec.PadSpace then
                    countTrailingSpaces b - countTrailingSpaces a
                else
                    0

    let comparePrimary (a: string) (b: string) : int =
        if String.Equals(a, b, StringComparison.Ordinal) then
            0
        elif spec.ByteOrder then
            compareBinary a b
        else
            ci.Compare(primaryText a, primaryText b, spec.Fold)

    { Name = name
      Compare = compareFull
      ComparePrimary = comparePrimary
      Equals = fun a b -> comparePrimary a b = 0
      KeyOf =
        fun s ->
            if spec.ByteOrder then
                "B" + Convert.ToHexString(binaryBytes s)
            else
                Convert.ToHexString(ci.GetSortKey(primaryText s, spec.Fold).KeyData)
      WeightOf =
        fun s ->
            if spec.ByteOrder then
                binaryWeight s
            else
                ci.GetSortKey(foldText s, spec.Fold).KeyData
      HashOf =
        fun s ->
            if spec.ByteOrder then
                hash (binaryBytes s)
            else
                ci.GetHashCode(primaryText s, spec.Fold)
      CharEquals =
        if spec.ByteOrder then
            fun a b -> binaryBytes (string a) = binaryBytes (string b)
        else
            fun a b -> ci.Compare(primaryText (string a), primaryText (string b), spec.Fold) = 0
      IsPrefix =
        if spec.ByteOrder then
            binaryPrefix
        else
            fun value prefix -> ci.IsPrefix(foldText value, foldText prefix, spec.Fold)
      PadSpace = spec.PadSpace }

// ---------------------------------------------------------------------------
// The registry — every utf8mb4 collation MySQL 8.4 ships.
// ---------------------------------------------------------------------------

let private aiCi = CompareOptions.IgnoreCase ||| CompareOptions.IgnoreNonSpace
let private asCi = CompareOptions.IgnoreCase
let private asCs = CompareOptions.None

let private noPad = false
let private pad = true

/// `(suffix, locale)` — every language shares one ICU locale; the
/// `_ai_ci`/`_as_cs` 0900 pairs differ only in folding.
let private language =
    [ "da", Some "da-DK"
      "nb", Some "nb-NO"
      "nn", Some "nn-NO"
      "sv", Some "sv-SE"
      "de_pb", Some "de-DE" // phonebook is ICU's default German order
      "tr", Some "tr-TR"
      "es", Some "es-ES"
      "es_trad", Some "es-ES_tradnl"
      "is", Some "is-IS"
      "et", Some "et-EE"
      "pl", Some "pl-PL"
      "ro", Some "ro-RO"
      "ru", Some "ru-RU"
      "sk", Some "sk-SK"
      "sl", Some "sl-SI"
      "vi", Some "vi-VN"
      "bg", Some "bg-BG"
      "bs", Some "bs-Latn-BA"
      "cs", Some "cs-CZ"
      "eo", Some "eo"
      "gl", Some "gl-ES"
      "hr", Some "hr-HR"
      "hu", Some "hu-HU"
      "la", Some "la"
      "lt", Some "lt-LT"
      "lv", Some "lv-LV"
      "mn_cyrl", Some "mn-MN"
      "sr_latn", Some "sr-Latn-RS"
      "ja", Some "ja-JP"
      "zh", Some "zh-Hans-CN" ]

let private register (name: string) (spec: Spec) (map: Map<string, Collation>) : Map<string, Collation> =
    Map.add name (makeCollation name spec) map

let private additionalCharsetCollations =
    [ "big5_chinese_ci", 1, 1, "big5_bin", 84, Some "zh-Hant-TW"
      "cp1250_general_ci", 26, 1, "cp1250_bin", 66, None
      "cp1251_general_ci", 51, 1, "cp1251_bin", 50, Some "ru-RU"
      "cp1256_general_ci", 57, 1, "cp1256_bin", 67, Some "ar-SA"
      "cp1257_general_ci", 59, 1, "cp1257_bin", 58, None
      "cp850_general_ci", 4, 1, "cp850_bin", 80, None
      "cp852_general_ci", 40, 1, "cp852_bin", 81, None
      "cp866_general_ci", 36, 1, "cp866_bin", 68, Some "ru-RU"
      "cp932_japanese_ci", 95, 1, "cp932_bin", 96, Some "ja-JP"
      "euckr_korean_ci", 19, 1, "euckr_bin", 85, Some "ko-KR"
      "gb18030_chinese_ci", 248, 2, "gb18030_bin", 249, Some "zh-Hans-CN"
      "gbk_chinese_ci", 28, 1, "gbk_bin", 87, Some "zh-Hans-CN"
      "greek_general_ci", 25, 1, "greek_bin", 70, Some "el-GR"
      "hebrew_general_ci", 16, 1, "hebrew_bin", 71, Some "he-IL"
      "koi8r_general_ci", 7, 1, "koi8r_bin", 74, Some "ru-RU"
      "koi8u_general_ci", 22, 1, "koi8u_bin", 75, Some "uk-UA"
      "latin2_general_ci", 9, 1, "latin2_bin", 77, None
      "latin5_turkish_ci", 30, 1, "latin5_bin", 78, Some "tr-TR"
      "latin7_general_ci", 41, 1, "latin7_bin", 79, None
      "macce_general_ci", 38, 1, "macce_bin", 43, None
      "macroman_general_ci", 39, 1, "macroman_bin", 53, None
      "ucs2_general_ci", 35, 1, "ucs2_bin", 90, None
      "ujis_japanese_ci", 12, 1, "ujis_bin", 91, Some "ja-JP"
      "utf16_general_ci", 54, 1, "utf16_bin", 55, None
      "utf16le_general_ci", 56, 1, "utf16le_bin", 62, None
      "utf32_general_ci", 60, 1, "utf32_bin", 61, None ]

let registry : Map<string, Collation> =
    Map.empty
    // The 0900 attribute matrix (NO PAD)
    |> register "utf8mb4_0900_ai_ci" { Locale = None; Fold = aiCi; PadSpace = noPad; ByteOrder = false }
    |> register "utf8mb4_0900_as_ci" { Locale = None; Fold = asCi; PadSpace = noPad; ByteOrder = false }
    |> register "utf8mb4_0900_as_cs" { Locale = None; Fold = asCs; PadSpace = noPad; ByteOrder = false }
    |> register "utf8mb4_0900_bin" { Locale = None; Fold = asCs; PadSpace = noPad; ByteOrder = true }
    // Legacy defaults
    |> register "utf8mb4_unicode_ci" { Locale = None; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_unicode_520_ci" { Locale = None; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_general_ci" { Locale = None; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_bin" { Locale = None; Fold = asCs; PadSpace = pad; ByteOrder = true }
    // Language-specific 0900 pairs
    |> fun map ->
        language
        |> List.fold
            (fun m (suffix, locale) ->
                // MySQL ships no `_0900_ai_ci` for ja/zh (kana/hanzi
                // sensitivity makes accent-insensitivity meaningless there) —
                // registering one would resolve a collation a real server
                // rejects with error 1273.
                let m =
                    if suffix = "ja" || suffix = "zh" then
                        m
                    else
                        m |> register ("utf8mb4_" + suffix + "_0900_ai_ci") { Locale = locale; Fold = aiCi; PadSpace = noPad; ByteOrder = false }

                m |> register ("utf8mb4_" + suffix + "_0900_as_cs") { Locale = locale; Fold = asCs; PadSpace = noPad; ByteOrder = false })
            map
    |> register "utf8mb4_ja_0900_as_cs_ks" { Locale = Some "ja-JP"; Fold = asCs; PadSpace = noPad; ByteOrder = false }
    // Legacy language collations (PAD SPACE, folded, _ci only)
    |> register "utf8mb4_danish_ci" { Locale = Some "da-DK"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_swedish_ci" { Locale = Some "sv-SE"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_german2_ci" { Locale = Some "de-DE"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_spanish_ci" { Locale = Some "es-ES"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_spanish2_ci" { Locale = Some "es-ES_tradnl"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_turkish_ci" { Locale = Some "tr-TR"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_icelandic_ci" { Locale = Some "is-IS"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_estonian_ci" { Locale = Some "et-EE"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_polish_ci" { Locale = Some "pl-PL"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_romanian_ci" { Locale = Some "ro-RO"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_roman_ci" { Locale = Some "ro-RO"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_croatian_ci" { Locale = Some "hr-HR"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_czech_ci" { Locale = Some "cs-CZ"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_esperanto_ci" { Locale = Some "eo"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_hungarian_ci" { Locale = Some "hu-HU"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_latvian_ci" { Locale = Some "lv-LV"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_lithuanian_ci" { Locale = Some "lt-LT"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_persian_ci" { Locale = Some "fa-IR"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_sinhala_ci" { Locale = Some "si-LK"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_slovak_ci" { Locale = Some "sk-SK"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_slovenian_ci" { Locale = Some "sl-SI"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_vietnamese_ci" { Locale = Some "vi-VN"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    // Legacy-charset collations (utf8mb3/latin1/ascii/binary) — clients and
    // GUI tools compare information_schema strings with `COLLATE utf8_bin`
    // and columns declared in these charsets; values are stored as .NET
    // strings either way, so only the comparison semantics differ.
    |> register "utf8mb3_general_ci" { Locale = None; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb3_unicode_ci" { Locale = None; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb3_tolower_ci" { Locale = None; Fold = asCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb3_bin" { Locale = None; Fold = asCs; PadSpace = pad; ByteOrder = true }
    |> register "latin1_swedish_ci" { Locale = None; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "latin1_general_ci" { Locale = None; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "latin1_bin" { Locale = None; Fold = asCs; PadSpace = pad; ByteOrder = true }
    |> register "ascii_general_ci" { Locale = None; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "ascii_bin" { Locale = None; Fold = asCs; PadSpace = pad; ByteOrder = true }
    |> register "binary" { Locale = None; Fold = asCs; PadSpace = noPad; ByteOrder = true }
    |> fun map ->
        additionalCharsetCollations
        |> List.fold (fun map (defaultName, _, _, binaryName, _, locale) ->
            map
            |> register defaultName { Locale = locale; Fold = aiCi; PadSpace = pad; ByteOrder = false }
            |> register binaryName { Locale = locale; Fold = asCs; PadSpace = pad; ByteOrder = true }) map

/// MySQL 8.4's `information_schema.collations` `ID` and `SORTLEN` for every
/// registered collation — harvested from a real 8.4.11 server
/// (`SELECT collation_name, id, sortlen FROM information_schema.collations`)
/// so `information_schema.COLLATIONS` and `SHOW COLLATION` report the same
/// values a client sees against real MySQL. Keep in lockstep with `registry`;
/// the test suite asserts the two key sets match exactly.
let idAndSortlen : Map<string, int * int> =
    Map.ofList
        [ "utf8mb4_0900_ai_ci", (255, 0)
          "utf8mb4_0900_as_ci", (305, 0)
          "utf8mb4_0900_as_cs", (278, 0)
          "utf8mb4_0900_bin", (309, 1)
          "utf8mb4_bin", (46, 1)
          "utf8mb4_general_ci", (45, 1)
          "utf8mb4_unicode_ci", (224, 8)
          "utf8mb4_unicode_520_ci", (246, 8)
          "utf8mb4_bg_0900_ai_ci", (318, 0)
          "utf8mb4_bg_0900_as_cs", (319, 0)
          "utf8mb4_bs_0900_ai_ci", (316, 0)
          "utf8mb4_bs_0900_as_cs", (317, 0)
          "utf8mb4_cs_0900_ai_ci", (266, 0)
          "utf8mb4_cs_0900_as_cs", (289, 0)
          "utf8mb4_da_0900_ai_ci", (267, 0)
          "utf8mb4_da_0900_as_cs", (290, 0)
          "utf8mb4_de_pb_0900_ai_ci", (256, 0)
          "utf8mb4_de_pb_0900_as_cs", (279, 0)
          "utf8mb4_eo_0900_ai_ci", (273, 0)
          "utf8mb4_eo_0900_as_cs", (296, 0)
          "utf8mb4_es_0900_ai_ci", (263, 0)
          "utf8mb4_es_0900_as_cs", (286, 0)
          "utf8mb4_es_trad_0900_ai_ci", (270, 0)
          "utf8mb4_es_trad_0900_as_cs", (293, 0)
          "utf8mb4_et_0900_ai_ci", (262, 0)
          "utf8mb4_et_0900_as_cs", (285, 0)
          "utf8mb4_gl_0900_ai_ci", (320, 0)
          "utf8mb4_gl_0900_as_cs", (321, 0)
          "utf8mb4_hr_0900_ai_ci", (275, 0)
          "utf8mb4_hr_0900_as_cs", (298, 0)
          "utf8mb4_hu_0900_ai_ci", (274, 0)
          "utf8mb4_hu_0900_as_cs", (297, 0)
          "utf8mb4_is_0900_ai_ci", (257, 0)
          "utf8mb4_is_0900_as_cs", (280, 0)
          "utf8mb4_ja_0900_as_cs", (303, 0)
          "utf8mb4_ja_0900_as_cs_ks", (304, 24)
          "utf8mb4_la_0900_ai_ci", (271, 0)
          "utf8mb4_la_0900_as_cs", (294, 0)
          "utf8mb4_lt_0900_ai_ci", (268, 0)
          "utf8mb4_lt_0900_as_cs", (291, 0)
          "utf8mb4_lv_0900_ai_ci", (258, 0)
          "utf8mb4_lv_0900_as_cs", (281, 0)
          "utf8mb4_mn_cyrl_0900_ai_ci", (322, 0)
          "utf8mb4_mn_cyrl_0900_as_cs", (323, 0)
          "utf8mb4_nb_0900_ai_ci", (310, 0)
          "utf8mb4_nb_0900_as_cs", (311, 0)
          "utf8mb4_nn_0900_ai_ci", (312, 0)
          "utf8mb4_nn_0900_as_cs", (313, 0)
          "utf8mb4_pl_0900_ai_ci", (261, 0)
          "utf8mb4_pl_0900_as_cs", (284, 0)
          "utf8mb4_ro_0900_ai_ci", (259, 0)
          "utf8mb4_ro_0900_as_cs", (282, 0)
          "utf8mb4_ru_0900_ai_ci", (306, 0)
          "utf8mb4_ru_0900_as_cs", (307, 0)
          "utf8mb4_sk_0900_ai_ci", (269, 0)
          "utf8mb4_sk_0900_as_cs", (292, 0)
          "utf8mb4_sl_0900_ai_ci", (260, 0)
          "utf8mb4_sl_0900_as_cs", (283, 0)
          "utf8mb4_sr_latn_0900_ai_ci", (314, 0)
          "utf8mb4_sr_latn_0900_as_cs", (315, 0)
          "utf8mb4_sv_0900_ai_ci", (264, 0)
          "utf8mb4_sv_0900_as_cs", (287, 0)
          "utf8mb4_tr_0900_ai_ci", (265, 0)
          "utf8mb4_tr_0900_as_cs", (288, 0)
          "utf8mb4_vi_0900_ai_ci", (277, 0)
          "utf8mb4_vi_0900_as_cs", (300, 0)
          "utf8mb4_zh_0900_as_cs", (308, 0)
          "utf8mb4_croatian_ci", (245, 8)
          "utf8mb4_czech_ci", (234, 8)
          "utf8mb4_danish_ci", (235, 8)
          "utf8mb4_esperanto_ci", (241, 8)
          "utf8mb4_estonian_ci", (230, 8)
          "utf8mb4_german2_ci", (244, 8)
          "utf8mb4_hungarian_ci", (242, 8)
          "utf8mb4_icelandic_ci", (225, 8)
          "utf8mb4_latvian_ci", (226, 8)
          "utf8mb4_lithuanian_ci", (236, 8)
          "utf8mb4_persian_ci", (240, 8)
          "utf8mb4_polish_ci", (229, 8)
          "utf8mb4_roman_ci", (239, 8)
          "utf8mb4_romanian_ci", (227, 8)
          "utf8mb4_sinhala_ci", (243, 8)
          "utf8mb4_slovak_ci", (237, 8)
          "utf8mb4_slovenian_ci", (228, 8)
          "utf8mb4_spanish2_ci", (238, 8)
          "utf8mb4_spanish_ci", (231, 8)
          "utf8mb4_swedish_ci", (232, 8)
          "utf8mb4_turkish_ci", (233, 8)
          "utf8mb4_vietnamese_ci", (247, 8)
          "utf8mb3_general_ci", (33, 1)
          "utf8mb3_unicode_ci", (192, 8)
          "utf8mb3_tolower_ci", (76, 1)
          "utf8mb3_bin", (83, 1)
          "latin1_swedish_ci", (8, 1)
          "latin1_general_ci", (48, 1)
          "latin1_bin", (47, 1)
          "ascii_general_ci", (11, 1)
          "ascii_bin", (65, 1)
          "binary", (63, 1) ]
    |> fun map ->
        additionalCharsetCollations
        |> List.fold (fun map (defaultName, defaultId, defaultSortlen, binaryName, binaryId, _) ->
            map
            |> Map.add defaultName (defaultId, defaultSortlen)
            |> Map.add binaryName (binaryId, 1)) map

/// Looks a collation up by its MySQL name — `COLLATE x` resolution and
/// column definitions both route through here. `utf8_*` resolves as MySQL's
/// deprecated alias for `utf8mb3_*` (accepted in SQL, listed only under the
/// canonical name).
let private canonicalName (name: string) =
    let lower = name.ToLowerInvariant()

    if lower.StartsWith "utf8_" then "utf8mb3_" + lower.Substring 5 else lower

let tryFind (name: string) : Collation option =
    Map.tryFind (canonicalName name) registry

let tryId (name: string) : int option =
    idAndSortlen |> Map.tryFind (canonicalName name) |> Option.map fst

/// Conservative maximum weight bytes produced per requested CHAR scalar.
/// MySQL's legacy utf8 binary weights use three bytes despite reporting a
/// one-byte SORTLEN; 0900 collations report zero for their variable format,
/// whose server-side transformation budget is sixteen bytes per scalar.
let weightBytesPerCharacter (name: string) =
    let name = canonicalName name

    if name = "utf8mb3_bin" || name = "utf8mb4_bin" then
        3
    elif name = "binary" then
        1
    elif name.EndsWith("_bin", StringComparison.Ordinal) then
        let separator = name.IndexOf '_'
        let charset = if separator < 0 then name else name.Substring(0, separator)
        Charset.maxBytes charset |> Option.defaultValue 4
    else
        match idAndSortlen |> Map.tryFind name with
        | Some(_, sortLength) when sortLength > 16 -> sortLength
        | _ -> 16

let tryFindById (id: int) : Collation option =
    idAndSortlen
    |> Seq.tryPick (fun (KeyValue(name, (candidate, _))) ->
        if candidate = id then tryFind name else None)

/// The charset a collation name belongs to — the prefix before the suffix
/// MySQL appends (`binary` is its own one-collation pseudo charset).
let charsetOfCollation (name: string) : string =
    match name.ToLowerInvariant() with
    | "binary" -> "binary"
    | lower ->
        match lower.IndexOf '_' with
        | -1 -> lower
        | i -> lower.Substring(0, i)

let defaultNameForCharset (charset: string) =
    Charset.defaultCollationName charset |> Option.defaultValue "utf8mb4_0900_ai_ci"

let belongsToCharset (charset: string) (collation: string) =
    let charset = Charset.canonicalName charset
    let owner = charsetOfCollation collation
    owner = charset

let maxBytesPerCharacter (charset: string option) =
    charset |> Option.bind Charset.maxBytes |> Option.defaultValue 4

/// The engine's one active default — a `Store`-level default today, the
/// seam a per-session/per-column `COLLATE` resolves against.
let defaultCollation = Map.find "utf8mb4_0900_ai_ci" registry
let metadataIdentifierCollation = Map.find "utf8mb3_tolower_ci" registry
