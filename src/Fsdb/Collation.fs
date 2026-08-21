/// String collation semantics — the single home for how fsdb compares and
/// sorts text. Everything that needs string semantics delegates here;
/// nothing re-derives its own rules per call site.
///
/// The full utf8mb4 collation set MySQL 8.4 ships is registered below
/// (all 89), driven by ICU collation — the same weight family UCA defines,
/// so *equality* (what indexes, unique keys, and joins depend on) matches
/// MySQL exactly. Each collation is data: an ICU locale (or byte order for
/// the `_bin` pair), the sensitivity folding (ai/ci combos), and the pad
/// attribute (PAD SPACE vs NO PAD — MySQL-verified: PAD SPACE collations
/// ignore trailing spaces in equality but sort `'a '` *before* `'a'`).
///
/// ponytails:
///  - the exact tie-break *order* among accent variants under ORDER BY may
///    differ from MySQL's own weight table, because the host ICU's CLDR
///    version differs (Apple ICU vs UCA 9.0/CLDR 30); equality never does.
///  - `utf8mb4_unicode_520_ci`/legacy language collations use ICU's CLDR
///    tailoring rather than MySQL's UCA 5.2/4.0 weight tables.
///  - LIKE folds per character and never expands ('æ' LIKE 'ae' is false
///    while 'æ' = 'ae' is true — MySQL-verified) — see `Executor.likeMatch`.
///  - REGEXP stays accent-sensitive; documented at `Executor.regexpOp`.
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
      /// `_bin`: compare by UTF-8 code-unit bytes, not ICU weights.
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
    let foldText (value: string) =
        if name = "utf8mb4_general_ci" then
            value.Replace("ß", "s").Replace("ẞ", "s")
        else
            value

    let primaryText (s: string) = trim s |> foldText

    let binaryWeight (s: string) =
        if name.StartsWith("utf8mb", StringComparison.Ordinal) then
            s.EnumerateRunes()
            |> Seq.collect (fun rune ->
                let value = rune.Value
                [ byte (value >>> 16); byte (value >>> 8); byte value ])
            |> Array.ofSeq
        else
            Text.Encoding.UTF8.GetBytes s

    let compareFull (a: string) (b: string) : int =
        if spec.ByteOrder then
            let c = String.Compare(trim a, trim b, StringComparison.Ordinal)
            // trimmed-equal under PAD SPACE: the extra-space side sorts
            // first (MySQL-verified), same tie-break as the ICU branch.
            if c <> 0 then c else countTrailingSpaces b - countTrailingSpaces a
        else
            if not spec.PadSpace then
                ci.Compare(a, b, CompareOptions.None)
            else
                let trimmed = ci.Compare(trim a, trim b, CompareOptions.None)
                if trimmed <> 0 then trimmed else countTrailingSpaces b - countTrailingSpaces a

    let comparePrimary (a: string) (b: string) : int =
        if spec.ByteOrder then
            String.Compare(trim a, trim b, StringComparison.Ordinal)
        else
            ci.Compare(primaryText a, primaryText b, spec.Fold)

    { Name = name
      Compare = compareFull
      ComparePrimary = comparePrimary
      Equals = fun a b -> comparePrimary a b = 0
      KeyOf =
        fun s ->
            if spec.ByteOrder then
                "B" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(trim s))
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
                StringComparer.Ordinal.GetHashCode(trim s)
            else
                ci.GetHashCode(primaryText s, spec.Fold)
      CharEquals =
        if spec.ByteOrder then
            fun a b -> a = b
        else
            fun a b -> ci.Compare(primaryText (string a), primaryText (string b), spec.Fold) = 0
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
    |> register "utf8mb3_bin" { Locale = None; Fold = asCs; PadSpace = pad; ByteOrder = true }
    |> register "latin1_swedish_ci" { Locale = None; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "latin1_general_ci" { Locale = None; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "latin1_bin" { Locale = None; Fold = asCs; PadSpace = pad; ByteOrder = true }
    |> register "ascii_general_ci" { Locale = None; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "ascii_bin" { Locale = None; Fold = asCs; PadSpace = pad; ByteOrder = true }
    |> register "binary" { Locale = None; Fold = asCs; PadSpace = noPad; ByteOrder = true }

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
          "utf8mb3_bin", (83, 1)
          "latin1_swedish_ci", (8, 1)
          "latin1_general_ci", (48, 1)
          "latin1_bin", (47, 1)
          "ascii_general_ci", (11, 1)
          "ascii_bin", (65, 1)
          "binary", (63, 1) ]

/// Looks a collation up by its MySQL name — `COLLATE x` resolution and
/// column definitions both route through here. `utf8_*` resolves as MySQL's
/// deprecated alias for `utf8mb3_*` (accepted in SQL, listed only under the
/// canonical name).
let tryFind (name: string) : Collation option =
    let lower = name.ToLowerInvariant()

    let canonical =
        if lower.StartsWith "utf8_" then "utf8mb3_" + lower.Substring 5 else lower

    Map.tryFind canonical registry

/// The charset a collation name belongs to — the prefix before the suffix
/// MySQL appends (`binary` is its own one-collation pseudo charset).
let charsetOfCollation (name: string) : string =
    match name.ToLowerInvariant() with
    | "binary" -> "binary"
    | lower ->
        match lower.IndexOf '_' with
        | -1 -> lower
        | i -> lower.Substring(0, i)

/// The engine's one active default — a `Store`-level default today, the
/// seam a per-session/per-column `COLLATE` resolves against.
let defaultCollation = Map.find "utf8mb4_0900_ai_ci" registry

// ---------------------------------------------------------------------------
// Charset write-time transcoding. MySQL's `latin1` is really cp1252, not
// ISO-8859-1: `€` stores fine as byte 0x80, and the 0x80–0x9F range holds
// printable punctuation (quotes, dashes, ™, …) where ISO-8859-1 has control
// characters — verified against 8.4. `ascii` is 7-bit. Both map anything
// unencodable to '?' rather than erroring (`ascii` columns still reject in
// strict mode at the storage layer; this mapping is the lossy fallback).
// ---------------------------------------------------------------------------

/// The Unicode code points cp1252 adds above ISO-8859-1, in 0x80–0x9F slot
/// order (0x81/0x8D/0x8F/0x90/0x9D are undefined in cp1252 and skipped).
module Charset =
    /// cp1252's 0x80–0x9F slots, in slot order — the five slots cp1252
    /// leaves undefined (0x81/0x8D/0x8F/0x90/0x9D) are `None`.
    let private cp1252HighSlots : char option array =
        [| Some '€' // 0x80
           None // 0x81
           Some '‚' // 0x82
           Some 'ƒ' // 0x83
           Some '„' // 0x84
           Some '…' // 0x85
           Some '†' // 0x86
           Some '‡' // 0x87
           Some 'ˆ' // 0x88
           Some '‰' // 0x89
           Some 'Š' // 0x8A
           Some '‹' // 0x8B
           Some 'Œ' // 0x8C
           None // 0x8D
           Some 'Ž' // 0x8E
           None // 0x8F
           None // 0x90
           Some '‘' // 0x91
           Some '’' // 0x92
           Some '“' // 0x93
           Some '”' // 0x94
           Some '•' // 0x95
           Some '–' // 0x96
           Some '—' // 0x97
           Some '˜' // 0x98
           Some '™' // 0x99
           Some 'š' // 0x9A
           Some '›' // 0x9B
           Some 'œ' // 0x9C
           None // 0x9D
           Some 'ž' // 0x9E
           Some 'Ÿ' |] // 0x9F

    let private cp1252Extras = cp1252HighSlots |> Array.choose id |> Array.map int |> Set.ofArray

    let private cp1252ByteByCode =
        cp1252HighSlots
        |> Array.indexed
        |> Array.choose (fun (index, value) -> value |> Option.map (fun value -> int value, byte (index + 0x80)))
        |> Map.ofArray

    /// Maps text to what a `latin1` (cp1252) column can hold: ASCII and
    /// 0xA0–0xFF pass through, the cp1252 extras pass through, everything
    /// else (including the C1 range 0x80–0x9F) becomes '?'. The engine
    /// stores text, not bytes, so a representable char keeps its Unicode
    /// form — `€` reads back as `€`, exactly what MySQL displays.
    let transcodeLatin1 (s: string) : string =
        s
        |> _.EnumerateRunes()
        |> Seq.map (fun rune ->
            let code = rune.Value

            if code < 0x80 || (code >= 0xA0 && code <= 0xFF) || cp1252Extras.Contains code then
                rune.ToString()
            else
                "?")
        |> String.concat ""

    /// Decodes raw bytes as cp1252 — what a `_latin1'...'` introducer needs,
    /// since MySQL labels the literal's client-encoded bytes without
    /// converting them (verified: `_latin1'é'` reads back as the two cp1252
    /// chars `Ã©`).
    let decodeLatin1Bytes (bytes: byte[]) : string =
        bytes
        |> Array.map (fun b ->
            if b < 0x80uy then
                char (int b)
            elif b >= 0xA0uy then
                char (int b)
            else
                match cp1252HighSlots.[int b - 0x80] with
                | Some c -> c
                | None -> '?')
        |> System.String

    /// Maps text to what an `ascii` column can hold: 7-bit passes through,
    /// everything else becomes '?'.
    let transcodeAscii (s: string) : string =
        s
        |> _.EnumerateRunes()
        |> Seq.map (fun rune -> if rune.Value < 0x80 then rune.ToString() else "?")
        |> String.concat ""

    /// Encodes a text value with the byte mapping MySQL applies before a
    /// binary string operation observes a column's character set.
    let encode (charset: string) (text: string) : byte[] =
        let byteForRune (rune: Rune) : byte =
            match charset with
            | "latin1" ->
                let code = rune.Value

                if code < 0x80 || (code >= 0xA0 && code <= 0xFF) then
                    byte code
                else
                    Map.tryFind code cp1252ByteByCode |> Option.defaultValue 0x3Fuy
            | "ascii" ->
                if rune.Value < 0x80 then byte rune.Value else 0x3Fuy
            | _ -> 0uy

        match charset with
        | "latin1"
        | "ascii" -> text.EnumerateRunes() |> Seq.map byteForRune |> Array.ofSeq
        | _ -> Text.Encoding.UTF8.GetBytes text

    /// Decodes raw bytes as ASCII — the `_ascii'...'` introducer's byte
    /// labeling, where each non-7-bit byte becomes one '?' (verified:
    /// `_ascii'å'` reads back as `??`, one per byte).
    let decodeAsciiBytes (bytes: byte[]) : string =
        bytes |> Array.map (fun b -> if b < 0x80uy then char (int b) else '?') |> System.String
