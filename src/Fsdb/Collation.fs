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
///  - `utf8mb4_general_ci` does no ß expansion in MySQL but ICU's folding
///    does ('ß' = 'ss' here, ≠ in real general_ci) — the one known
///    expansion divergence; everything else general_ci folds matches.
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
            ci.Compare(trim a, trim b, spec.Fold)

    { Name = name
      Compare = compareFull
      ComparePrimary = comparePrimary
      Equals = fun a b -> comparePrimary a b = 0
      KeyOf =
        fun s ->
            if spec.ByteOrder then
                "B" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(trim s))
            else
                Convert.ToHexString(ci.GetSortKey(trim s, spec.Fold).KeyData)
      HashOf =
        fun s ->
            if spec.ByteOrder then
                StringComparer.Ordinal.GetHashCode(trim s)
            else
                ci.GetHashCode(trim s, spec.Fold)
      CharEquals =
        if spec.ByteOrder then
            fun a b -> a = b
        else
            fun a b -> ci.Compare(string a, string b, spec.Fold) = 0
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
                m
                |> register ("utf8mb4_" + suffix + "_0900_ai_ci") { Locale = locale; Fold = aiCi; PadSpace = noPad; ByteOrder = false }
                |> register ("utf8mb4_" + suffix + "_0900_as_cs") { Locale = locale; Fold = asCs; PadSpace = noPad; ByteOrder = false })
            map
    |> register "utf8mb4_ja_0900_as_cs_ks" { Locale = Some "ja-JP"; Fold = asCs; PadSpace = noPad; ByteOrder = false }
    // Legacy language collations (PAD SPACE, folded, _ci only)
    |> register "utf8mb4_danish_ci" { Locale = Some "da-DK"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_swedish_ci" { Locale = Some "sv-SE"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
    |> register "utf8mb4_norwegian_ci" { Locale = Some "nb-NO"; Fold = aiCi; PadSpace = pad; ByteOrder = false }
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

/// Looks a collation up by its MySQL name — `COLLATE x` resolution and
/// column definitions both route through here.
let tryFind (name: string) : Collation option = Map.tryFind (name.ToLowerInvariant()) registry

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

    /// Maps text to what a `latin1` (cp1252) column can hold: ASCII and
    /// 0xA0–0xFF pass through, the cp1252 extras pass through, everything
    /// else (including the C1 range 0x80–0x9F) becomes '?'. The engine
    /// stores text, not bytes, so a representable char keeps its Unicode
    /// form — `€` reads back as `€`, exactly what MySQL displays.
    let transcodeLatin1 (s: string) : string =
        s
        |> String.map (fun c ->
            let code = int c

            if code < 0x80 || (code >= 0xA0 && code <= 0xFF) || cp1252Extras.Contains code then
                c
            else
                '?')

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
        s |> String.map (fun c -> if int c < 0x80 then c else '?')

    /// Decodes raw bytes as ASCII — the `_ascii'...'` introducer's byte
    /// labeling, where each non-7-bit byte becomes one '?' (verified:
    /// `_ascii'å'` reads back as `??`, one per byte).
    let decodeAsciiBytes (bytes: byte[]) : string =
        bytes |> Array.map (fun b -> if b < 0x80uy then char (int b) else '?') |> System.String
