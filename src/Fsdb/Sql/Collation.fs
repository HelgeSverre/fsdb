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
      /// `LOCATE`/`INSTR` prefix matching. MySQL applies case and linguistic
      /// expansion rules here, but keeps accents significant even for an
      /// accent-insensitive collation.
      IsSubstringPrefix: string -> string -> bool
      /// Fast substring search when both operands are ASCII, whose offsets
      /// are unchanged by the substring collation's normalization.
      FindSubstringAscii: string -> string -> int -> int
      /// PAD SPACE: trailing spaces are insignificant — `Equals` trims,
      /// and LIKE trims both subject and pattern ends before matching.
      PadSpace: bool
      /// Whether the full-order equivalence classes are exactly the SQL
      /// equality classes. A composite index may order by a suffix after an
      /// equality-fixed key only when this holds; otherwise case, accent, or
      /// padding variants can interleave separate suffix runs.
      EqualityIsOrderEquivalence: bool }

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

let private substringExpansions =
    [ "ß", "ss"
      "ẞ", "SS"
      "æ", "ae"
      "Æ", "AE"
      "œ", "oe"
      "Œ", "OE"
      "ĳ", "ij"
      "Ĳ", "IJ" ]

let private foldKanaVariants (value: string) =
    let isHalfwidthKana (c: char) = c >= '\uFF61' && c <= '\uFF9F'
    let isHiragana (c: char) = c >= '\u3041' && c <= '\u3096'
    let mutable first = 0

    while first < value.Length && not (isHalfwidthKana value.[first] || isHiragana value.[first]) do
        first <- first + 1

    if first = value.Length then
        value
    else
        let result = StringBuilder(value.Length)
        result.Append(value, 0, first) |> ignore
        let mutable index = first

        while index < value.Length do
            if isHalfwidthKana value.[index] then
                let start = index

                while index < value.Length && isHalfwidthKana value.[index] do
                    index <- index + 1

                result.Append(value.Substring(start, index - start).Normalize(NormalizationForm.FormKC)) |> ignore
            elif isHiragana value.[index] then
                result.Append(char (int value.[index] + 0x60)) |> ignore
                index <- index + 1
            else
                result.Append(value.[index]) |> ignore
                index <- index + 1

        result.ToString()

// ICU's Japanese sort key merges kana scripts even for MySQL's `_ks` variant.
// Compose dakuten first so both spellings of a voiced hiragana receive one key.
let private markJapaneseHiragana (value: string) =
    let value =
        if value.Contains('\u3099') || value.Contains('\u309A') then
            value.Normalize(NormalizationForm.FormC)
        else value
    let marker = '\uE0B8'
    let isHiragana (c: char) =
        (c >= '\u3041' && c <= '\u3096') || c = '\u309D' || c = '\u309E'
    let mutable first = 0
    while first < value.Length && not (isHiragana value.[first] || value.[first] = marker) do
        first <- first + 1

    if first = value.Length then value
    else
        let result = StringBuilder(value.Length + 4)
        result.Append(value, 0, first) |> ignore
        for index in first .. value.Length - 1 do
            let c = value.[index]
            result.Append(c) |> ignore
            if isHiragana c || c = marker then result.Append(marker) |> ignore
        result.ToString()

// MySQL 8.4 Japanese UCA weights distinguish most compatibility ideographs
// from their NFKC forms. These tables retain its measured weight classes.
let private japaneseCompatEqual =
    set [
    0xF927; 0xF930; 0xF93D; 0xF93E; 0xF93F; 0xF94B; 0xF94D; 0xF964;
    0xF96F; 0xF98B; 0xF98C; 0xF994; 0xF995; 0xF99B; 0xF9A1; 0xF9A5;
    0xF9C8; 0xF9EF; 0xF9FA; 0xFA0D; 0xFA15; 0xFA20; 0xFA2E; 0xFA49;
    0xFA5D; 0xFA5E; 0xFA66; 0xFA6C; 0xFA73; 0xFA79; 0xFA7B; 0xFA81;
    0xFA82; 0xFA83; 0xFA8A; 0xFA8F; 0xFA98; 0xFA9D; 0xFAA1; 0xFAA2;
    0xFAA4; 0xFAA9; 0xFAAB; 0xFAAC; 0xFAAE; 0xFAB1; 0xFAB5; 0xFAC4;
    0xFAC5; 0xFAC6; 0xFAC9; 0xFACB; 0xFACD; 0xFACF; 0xFAD0; 0xFAD1;
    0xFAD2; 0xFAD3; 0xFAD4; 0xFAD5; 0xFAD6; 0xFAD7; 0xFAD8; 0xFAD9; ]

let private japaneseCompatAliases =
    Map.ofList [
    0xF908, 0xF907; 0xFACE, 0xF907; 0xF95C, 0xF914;
    0xF9BF, 0xF914; 0xFA92, 0xF929; 0xFABD, 0xF95D;
    0xF9AA, 0xF95F; 0xF9DB, 0xF961; 0xFA96, 0xF970;
    0xFA57, 0xF996; 0xFAB0, 0xF996; 0xFA97, 0xF9CA;
    0xFA7C, 0xFA10; 0xFA91, 0xFA12; 0xFAA0, 0xFA16;
    0xFAA6, 0xFA17; 0xFAC8, 0xFA1C; 0xFABA, 0xFA22;
    0xFA67, 0xFA25; 0xFA78, 0xFA36; 0xFA89, 0xFA3F;
    0xFA8B, 0xFA40; 0xFA9A, 0xFA47; 0xFA9C, 0xFA48;
    0xFAAD, 0xFA56; 0xFAB2, 0xFA5B; 0xFAB8, 0xFA61;
    0xFABC, 0xFA62; 0xFABF, 0xFA63; 0xFAC1, 0xFA65;
    0xFAC7, 0xFA68; 0xFACA, 0xFA69; 0xFACC, 0xFA6A; ]

let private japaneseCompatSupplementaryAliases =
    Map.ofList [
    0x2F8B1, 0xF90D; 0x2F88E, 0xF928; 0x2F8D8, 0xF929;
    0x2F9B4, 0xF936; 0x2F94F, 0xF93B; 0x2F86F, 0xF95F;
    0x2F938, 0xF962; 0x2F82B, 0xF963; 0x2F8F5, 0xF970;
    0x2F998, 0xF974; 0x2F902, 0xF9CA; 0x2F850, 0xFA00;
    0x2F956, 0xFA1B; 0x2F805, 0xFA30; 0x2F80A, 0xFA31;
    0x2F80E, 0xFA32; 0x2F826, 0xFA33; 0x2F827, 0xFA34;
    0x2F82D, 0xFA35; 0x2F84C, 0xFA37; 0x2F878, 0xFA3C;
    0x2F8A3, 0xFA3D; 0x2F8AB, 0xFA3F; 0x2F8B0, 0xFA40;
    0x2F8C8, 0xFA41; 0x2F8CF, 0xFA43; 0x2F8E2, 0xFA44;
    0x2F901, 0xFA45; 0x2F953, 0xFA50; 0x2F959, 0xFA54;
    0x2F97A, 0xFA5B; 0x2F99F, 0xFA5F; 0x2F81B, 0xFA71;
    0x2F825, 0xFA76; 0x2F828, 0xFA77; 0x2F847, 0xFA7A;
    0x2F8A8, 0xFA87; 0x2F8D9, 0xFA93; 0x2F90B, 0xFA99;
    0x2F914, 0xFA9B; 0x2F921, 0xFA9E; 0x2F940, 0xFAA8;
    0x2F9D0, 0xFABE; 0x2F9D1, 0xFAC0; 0x2F9DF, 0xFAC2; ]

let private foldJapaneseCompatibility (value: string) =
    let marker = '\uE0B9'
    let classKey codePoint =
        let canonical = japaneseCompatAliases |> Map.tryFind codePoint |> Option.defaultValue codePoint
        string marker + string (char (0xE500 + canonical - 0xF900))
    let result = StringBuilder(value.Length)
    let mutable index = 0
    while index < value.Length do
        let c = value.[index]
        if c = marker then
            result.Append(marker).Append(marker) |> ignore
            index <- index + 1
        elif c >= '\uF900' && c <= '\uFAFF'
             && CharUnicodeInfo.GetUnicodeCategory(c) <> UnicodeCategory.OtherNotAssigned
             && not (japaneseCompatEqual.Contains(int c)) then
            result.Append(classKey (int c)) |> ignore
            index <- index + 1
        elif Char.IsHighSurrogate c && index + 1 < value.Length && Char.IsLowSurrogate value.[index + 1] then
            let codePoint = Char.ConvertToUtf32(c, value.[index + 1])
            match japaneseCompatSupplementaryAliases |> Map.tryFind codePoint with
            | Some canonical -> result.Append(classKey canonical) |> ignore
            | None -> result.Append(c).Append(value.[index + 1]) |> ignore
            index <- index + 2
        else
            result.Append(c) |> ignore
            index <- index + 1
    result.ToString()

let private foldSmallKana (value: string) =
    let regular = function
        | 'ァ' -> 'ア' | 'ィ' -> 'イ' | 'ゥ' -> 'ウ' | 'ェ' -> 'エ' | 'ォ' -> 'オ'
        | 'ッ' -> 'ツ' | 'ャ' -> 'ヤ' | 'ュ' -> 'ユ' | 'ョ' -> 'ヨ' | 'ヮ' -> 'ワ'
        | 'ヵ' -> 'カ' | 'ヶ' -> 'ケ'
        | c -> c

    let mutable first = 0
    while first < value.Length && regular value.[first] = value.[first] do
        first <- first + 1

    if first = value.Length then value
    else
        let result = StringBuilder(value.Length)
        result.Append(value, 0, first) |> ignore
        for index in first .. value.Length - 1 do
            result.Append(regular value.[index]) |> ignore
        result.ToString()

// ICU folds curly and CJK quote forms into ordinary quotes. MySQL keeps each
// form at a distinct primary weight in its Unicode collations.
let private foldUnicodeQuoteForms (value: string) =
    let mutable first = 0
    while first < value.Length
          && value.[first] <> '\uE020'
          && (value.[first] < '\u2018' || value.[first] > '\u201F')
          && (value.[first] < '\u301D' || value.[first] > '\u301F') do
        first <- first + 1

    if first = value.Length then value
    else
        let result = StringBuilder(value.Length)
        result.Append(value, 0, first) |> ignore
        for index in first .. value.Length - 1 do
            match value.[index] with
            | '\uE020' -> result.Append("\uE020\uE020") |> ignore
            | '\u301D' -> result.Append("\uE020\uE021") |> ignore
            | '\u301E' -> result.Append("\uE020\uE022") |> ignore
            | '\u301F' -> result.Append("\uE020\uE023") |> ignore
            | '\u2018' -> result.Append("\uE020\uE024") |> ignore
            | '\u2019' -> result.Append("\uE020\uE025") |> ignore
            | '\u201A' -> result.Append("\uE020\uE026") |> ignore
            | '\u201B' -> result.Append("\uE020\uE027") |> ignore
            | '\u201C' -> result.Append("\uE020\uE028") |> ignore
            | '\u201D' -> result.Append("\uE020\uE029") |> ignore
            | '\u201E' -> result.Append("\uE020\uE02A") |> ignore
            | '\u201F' -> result.Append("\uE020\uE02B") |> ignore
            | c -> result.Append(c) |> ignore
        result.ToString()

// UCA 5.2 assigns U+180E a primary weight; ICU now treats it as ignorable.
let private foldUnicode520MongolianSeparator (value: string) =
    if not (value.Contains('\u180e') || value.Contains('\uE0A0')) then value
    else
        let result = StringBuilder(value.Length)
        for c in value do
            match c with
            | '\u180e' -> result.Append("\uE0A0\uE0A1") |> ignore
            | '\uE0A0' -> result.Append("\uE0A0\uE0A0") |> ignore
            | c -> result.Append(c) |> ignore
        result.ToString()

// UCA 5.2 ignores the first seven combining half marks at the primary level,
// but gives the remaining marks distinct weights. ICU ignores the whole block,
// which otherwise merges unrelated ngram postings.
let private foldUnicode520CombiningHalfMarks (value: string) =
    let marker = '\uE0A2'
    if not (value |> Seq.exists (fun c -> c = marker || (c >= '\uFE27' && c <= '\uFE2F'))) then value
    else
        let result = StringBuilder(value.Length)
        for c in value do
            if c = marker then result.Append(marker).Append(marker) |> ignore
            elif c >= '\uFE27' && c <= '\uFE2F' then
                result.Append(marker).Append(char (0xE0A3 + int c - 0xFE27)) |> ignore
            else result.Append(c) |> ignore
        result.ToString()

let private foldSupplementaryImplicit (marker: char) (highSurrogate: char) firstScalar firstWeight hasWeight (value: string) =
    if not (value.Contains marker || value.Contains highSurrogate) then value
    else
        let result = StringBuilder(value.Length)
        let mutable index = 0
        while index < value.Length do
            let c = value.[index]
            if c = marker then
                result.Append(marker).Append(marker) |> ignore
            elif c = highSurrogate && index + 1 < value.Length
                 && Char.IsLowSurrogate value.[index + 1] then
                let scalar = Char.ConvertToUtf32(c, value.[index + 1])
                if hasWeight scalar then
                    result.Append(marker).Append(char (firstWeight + scalar - firstScalar)) |> ignore
                    index <- index + 1
                else
                    result.Append(c).Append(value.[index + 1]) |> ignore
                    index <- index + 1
            else result.Append(c) |> ignore
            index <- index + 1
        result.ToString()

// MySQL's Unicode 5.2 collation gives assigned supplementary kana in
// U+1B000–U+1B16F distinct implicit weights. Its 9.0 collation gives the
// first two explicit weights and the remaining scalars distinct implicit
// weights. ICU merges some Hiragana/Katakana spellings, notably archaic WU.
let private foldSupplementaryKanaImplicit unicode520 value =
    let hasWeight scalar =
        scalar >= 0x1B000 && scalar < 0x1B170
        && (unicode520 || scalar >= 0x1B002)
    foldSupplementaryImplicit '\uE0B2' '\uD82C' 0x1B000 0xE600 hasWeight value

// UCA 5.2 gives these enclosed alphanumeric scalars implicit, distinct
// weights. Newer ICU folds many of them into letters or other enclosed forms.
// The ranges are the scalars whose MySQL 8.4 unicode_520_ci WEIGHT_STRING is
// FBC3 followed by the low 16 bits of the scalar.
let private unicode520EnclosedImplicitRanges =
    [| 0x1F10B, 0x1F10F; 0x1F12F, 0x1F130; 0x1F132, 0x1F13C
       0x1F13E, 0x1F13E; 0x1F140, 0x1F141; 0x1F143, 0x1F145
       0x1F147, 0x1F149; 0x1F14F, 0x1F156; 0x1F158, 0x1F15E
       0x1F160, 0x1F178; 0x1F17A, 0x1F17A; 0x1F17D, 0x1F17E
       0x1F180, 0x1F189; 0x1F18E, 0x1F18F; 0x1F191, 0x1F1FF |]

let private foldUnicode520EnclosedImplicit value =
    let hasWeight scalar =
        unicode520EnclosedImplicitRanges
        |> Array.exists (fun (first, last) -> scalar >= first && scalar <= last)
    foldSupplementaryImplicit '\uE0BE' '\uD83C' 0x1F100 0xEA00 hasWeight value

// MySQL's UCA 4.0 and 5.2 tables retain implicit primary weights for marks
// assigned later. UCA 4.0 gives all of U+1DC0–U+1DFF distinct weights; 5.2
// assigns some explicit or ignorable weights instead. UCA 4.0 also keeps
// U+20EB–U+20F0 distinct where 5.2 treats them as ignorable. In 5.2, U+1DCA
// shares U+036C's weight while ICU merges it with the distinct U+1DE3.
// Escape the marker so literal private-use text cannot alias a generated weight.
let private foldLegacyExtendedCombiningMarks unicode520 (value: string) =
    let marker = '\uE0AE'
    let needsFold c =
        (c >= '\u1AB0' && c <= '\u1AFF')
        || (if unicode520 then
                (c >= '\u1DE7' && c <= '\u1DFC') || c = '\u1DCA' || c = '\u1DE3'
            else (c >= '\u1DC0' && c <= '\u1DFF') || (c >= '\u20EB' && c <= '\u20F0'))
    if not (value |> Seq.exists (fun c -> c = marker || needsFold c)) then value
    else
        let result = StringBuilder(value.Length)
        for c in value do
            if c = marker then result.Append(marker).Append(marker) |> ignore
            elif c >= '\u1AB0' && c <= '\u1AFF' then
                result.Append(marker).Append(char (0xE400 + int c - 0x1AB0)) |> ignore
            elif not unicode520 && c >= '\u1DC0' && c <= '\u1DFF' then
                result.Append(marker).Append(char (0xE500 + int c - 0x1DC0)) |> ignore
            elif not unicode520 && c >= '\u20EB' && c <= '\u20F0' then
                result.Append(marker).Append(char (0xE580 + int c - 0x20EB)) |> ignore
            elif unicode520 && c >= '\u1DE7' && c <= '\u1DFC' then
                result.Append(marker).Append(char (0xE480 + int c - 0x1DE7)) |> ignore
            elif unicode520 && c = '\u1DCA' then result.Append('\u036C') |> ignore
            elif unicode520 && c = '\u1DE3' then result.Append(marker).Append('\uE4A0') |> ignore
            else result.Append(c) |> ignore
        result.ToString()

// ICU composes conjoining Hangul Jamo into syllables. MySQL's older Unicode
// collations retain their individual weights; each collation decides whether
// compatibility Jamo share them.
let private foldUnicodeJamo compatibility (value: string) =
    let isJamo c = c >= '\u1100' && c <= '\u11ff'
    let marker = '\uE0B0'
    if not (value |> Seq.exists (fun c -> c = marker || isJamo c || (compatibility c |> Option.isSome))) then value
    else
        let result = StringBuilder(value.Length)
        let appendJamo c =
            result.Append(marker).Append(char (0xE500 + int c - 0x1100)) |> ignore
        for c in value do
            if c = marker then result.Append(marker).Append(marker) |> ignore
            elif isJamo c then appendJamo c
            else
                match compatibility c with
                | Some decomposed -> for jamo in decomposed do appendJamo jamo
                | None -> result.Append(c) |> ignore
        result.ToString()

let private foldLegacyUnicodeJamo (value: string) =
    let compatibility c =
        if (c >= '\u3130' && c <= '\u318f') || (c >= '\uffa0' && c <= '\uffdc') then
            let decomposed = (string c).Normalize(NormalizationForm.FormKD)
            if not (String.IsNullOrEmpty decomposed)
               && (decomposed |> Seq.forall (fun jamo -> jamo >= '\u1100' && jamo <= '\u11ff')) then
                Some decomposed
            else None
        else None
    foldUnicodeJamo compatibility value

// general_ci leaves compatibility Jamo distinct from conjoining Jamo.
let private foldGeneralUnicodeJamo (value: string) = foldUnicodeJamo (fun _ -> None) value

// Every synthetic key has a private-use marker. Literal markers double so
// they cannot alias a generated weight, including beside other private-use text.
let private generalSyntheticPair c =
    let pair marker weight = ValueSome(struct (marker, weight))
    match c with
    | '\uE0C0' | '\uE0D0' | '\uE0E0' | '\uE300' | '\uE0F0' | '\uE600'
    | '\uE0B1' | '\uE0B2' | '\uE0B3' | '\uE0B4' | '\uE0B5'
    | '\uE0B6' | '\uE0B7' | '\uE0B8' | '\uE0B9' | '\uE0BA' | '\uE0BB' | '\uE0BC' | '\uE0BD' as marker ->
        pair marker marker
    // Halfwidth voicing and decomposed kana marks have their own positional weights.
    | '\uff9e' -> pair '\uE0C0' '\uE0C1'
    | '\uff9f' -> pair '\uE0C0' '\uE0C2'
    | '\u3099' -> pair '\uE0E0' '\uE0E1'
    | '\u309a' -> pair '\uE0E0' '\uE0E2'
    // ICU ignores or merges these format controls; MySQL keeps them distinct.
    | '\u034f' -> pair '\uE0D0' '\uE0D1'
    | '\u00a0' -> pair '\uE0D0' '\uE0D2'
    | '\u180e' -> pair '\uE0D0' '\uE0D3'
    | '\u200b' -> pair '\uE0D0' '\uE0D4'
    | '\u200c' -> pair '\uE0D0' '\uE0D5'
    | '\u200d' -> pair '\uE0D0' '\uE0D6'
    | '\u2060' -> pair '\uE0D0' '\uE0D7'
    | '\u3000' -> pair '\uE0D0' '\uE0D8'
    // Decomposed accents retain their written weight, apart from Greek iota.
    | c when c >= '\u0300' && c <= '\u036f' && c <> '\u0345' ->
        pair '\uE300' (char (0xE400 + int c - 0x0300))
    // Assigned CJK radicals do not share the corresponding ideograph weight.
    | c when (c >= '\u2e80' && c <= '\u2e99')
             || (c >= '\u2e9b' && c <= '\u2ef3')
             || (c >= '\u2f00' && c <= '\u2fd5') ->
        pair '\uE0F0' (char (0xE100 + int c - 0x2e80))
    // General Unicode keeps kana script, width, voicing, and size distinctions.
    | c when (c >= '\u3041' && c <= '\u3096') || (c >= '\u3099' && c <= '\u30ff') ->
        pair '\uE600' (char (0xE700 + int c - 0x3040))
    | c when c >= '\uff61' && c <= '\uff9f' ->
        pair '\uE600' (char (0xE900 + int c - 0xff61))
    // These compatibility blocks retain their written forms; circled letters
    // are the exception whose lowercase forms share uppercase weights.
    | c when c >= '\uf900' && c <= '\ufaff' ->
        pair '\uE0B1' (char (0xEA00 + int c - 0xF900))
    | c when c >= '\u3200' && c <= '\u33ff' ->
        pair '\uE0B2' (char (0xEC00 + int c - 0x3200))
    | c when c >= '\u2460' && c <= '\u24ff' ->
        let upper = if c >= '\u24d0' && c <= '\u24e9' then char (int c - 26) else c
        pair '\uE0B3' (char (0xEE00 + int upper - 0x2460))
    | c when c >= '\u3001' && c <= '\u303f' ->
        pair '\uE0B4' (char (0xEF00 + int c - 0x3000))
    // MySQL gives every vertical and small-form punctuation code point its
    // written primary weight, including the unassigned positions in the block.
    | c when c >= '\uFE10' && c <= '\uFE6F' ->
        pair '\uE0B8' (char (0xF300 + int c - 0xFE10))
    // Kanbun and Bopomofo extension letters have distinct legacy weights;
    // ICU merges a few pairs that MySQL keeps separate.
    | c when c >= '\u3190' && c <= '\u31BF' ->
        pair '\uE0B9' (char (0xF400 + int c - 0x3190))
    // The older Bopomofo block also has one weight per written code point.
    | c when c >= '\u3100' && c <= '\u318F' ->
        pair '\uE0BA' (char (0xF500 + int c - 0x3100))
    // MySQL retains the written weights of later combining marks; ICU
    // merges some of them before full-text postings are built.
    | c when c >= '\u1AB0' && c <= '\u1AFF' ->
        pair '\uE0BB' (char (0xF600 + int c - 0x1AB0))
    | c when c >= '\u1DC0' && c <= '\u1DFF' ->
        pair '\uE0BB' (char (0xF680 + int c - 0x1DC0))
    | c when c >= '\u20D0' && c <= '\u20FF' ->
        pair '\uE0BB' (char (0xF700 + int c - 0x20D0))
    // Variation selectors are significant under general_ci, including FE0F.
    | c when c >= '\uFE00' && c <= '\uFE0F' ->
        pair '\uE0BB' (char (0xF800 + int c - 0xFE00))
    // ICU folds several dash, quote, and script-number forms together;
    // MySQL's legacy collation gives each of these its written weight.
    | c when (c >= '\u2010' && c <= '\u201F')
             || (c >= '\u2070' && c <= '\u208F')
             || c = '\u2099' ->
        pair '\uE0BC' (char (0xF810 + int c - 0x2000))
    // MySQL's general collation keeps this Glagolitic/Coptic block in
    // written-code-point order; ICU case-folds many of its letter pairs.
    | c when c >= '\u2C00' && c <= '\u2CFF' ->
        pair '\uE0BD' (char (0xE500 + int c - 0x2C00))
    // Letterlike symbols, number forms, and arrows retain their code-point
    // weights, except lowercase Roman numerals share uppercase weights.
    | c when c >= '\u2100' && c <= '\u21ff' ->
        let upper = if c >= '\u2170' && c <= '\u217f' then char (int c - 16) else c
        pair '\uE0B5' (char (0xF000 + int upper - 0x2100))
    // Mathematical operators in this block keep one primary weight per code point.
    | c when c >= '\u2200' && c <= '\u22ff' ->
        pair '\uE0B6' (char (0xF100 + int c - 0x2200))
    // MySQL keeps dingbats and supplemental mathematical operators distinct.
    | c when (c >= '\u2700' && c <= '\u27ff') || (c >= '\u2a00' && c <= '\u2aff') ->
        pair '\uE0B7' (char (0xF200 + int c - 0x2700))
    | _ -> ValueNone

let private foldGeneralSyntheticWeights (value: string) =
    let mutable first = 0
    while first < value.Length
          && (match generalSyntheticPair value.[first] with ValueNone -> true | ValueSome _ -> false) do
        first <- first + 1
    if first = value.Length then value
    else
        let result = StringBuilder(value.Length)
        result.Append(value, 0, first) |> ignore
        for index in first .. value.Length - 1 do
            let c = value.[index]
            match generalSyntheticPair c with
            | ValueSome(struct (marker, weight)) -> result.Append(marker).Append(weight) |> ignore
            | ValueNone -> result.Append(c) |> ignore
        result.ToString()

// MySQL's legacy UCA keeps some enclosed Korean/ideograph forms distinct,
// while expanding enclosed kana and square unit forms that ICU keeps apart.
let private foldLegacyEnclosedCjk (value: string) =
    let isDistinct c =
        (c >= '\u320E' && c <= '\u321F')
        || (c >= '\u3244' && c <= '\u324F')
        || (c >= '\u326E' && c <= '\u327E')
        || c = '\u32FF'
    let isExpansion c =
        (c >= '\u3200' && c <= '\u320D')
        || (c >= '\u3260' && c <= '\u326D')
        || (c >= '\u32D0' && c <= '\u32FE')
        || (c >= '\u3300' && c <= '\u3357')
    if not (value |> Seq.exists (fun c -> c = '\uE030' || isDistinct c || isExpansion c)) then value
    else
        let result = StringBuilder(value.Length)
        for c in value do
            if c = '\uE030' then result.Append("\uE030\uE030") |> ignore
            elif isDistinct c then
                result.Append('\uE030').Append(char (0xE100 + int c - 0x3200)) |> ignore
            elif isExpansion c then
                result.Append((string c).Normalize(NormalizationForm.FormKC)) |> ignore
            else result.Append(c) |> ignore
        result.ToString()

// ICU merges a few letterlike/number forms that MySQL's UCA 4.0 assigns
// distinct primary weights. Escape the marker so literal private-use text
// cannot share one of these synthetic keys.
let private foldLegacyLetterlikeForms (value: string) =
    let isDistinct = function
        | '\u210F' | '\u2132' | '\u213C' | '\u214D' | '\u214E'
        | '\u2150' | '\u2151' | '\u2152'
        | '\u2183' | '\u2184' | '\u2189' -> true
        | _ -> false
    if not (value |> Seq.exists (fun c -> c = '\uE040' || c = '\u0127' || isDistinct c)) then value
    else
        let result = StringBuilder(value.Length)
        for c in value do
            if c = '\uE040' then result.Append("\uE040\uE040") |> ignore
            elif c = '\u0127' then result.Append("\uE040\uE20F") |> ignore
            elif isDistinct c then
                result.Append('\uE040').Append(char (0xE200 + int c - 0x2100)) |> ignore
            else result.Append(c) |> ignore
        result.ToString()

// UCA 4.0 expands kana ligatures and folds the small katakana extension
// before ordinary kana and accent folding.
let private foldLegacyKanaForms (value: string) =
    let ordinary = "クシストヌハヒフヘホムラリルレロ"
    if not (value |> Seq.exists (fun c -> c = 'ゟ' || c = 'ヿ' || (c >= '\u31F0' && c <= '\u31FF'))) then value
    else
        let result = StringBuilder(value.Length)
        for c in value do
            match c with
            | 'ゟ' -> result.Append("より") |> ignore
            | 'ヿ' -> result.Append("コト") |> ignore
            | c when c >= '\u31F0' && c <= '\u31FF' -> result.Append(ordinary.[int c - 0x31F0]) |> ignore
            | c -> result.Append(c) |> ignore
        result.ToString()

// ICU merges these Bopomofo forms with older letters; MySQL's Unicode 4.0
// and 5.2 collations keep them at distinct primary weights.
let private foldLegacyBopomofoForms (value: string) =
    if not (value |> Seq.exists (fun c -> c = '\uE060' || c = 'ㄮ' || c = 'ㆻ')) then value
    else
        let result = StringBuilder(value.Length)
        for c in value do
            match c with
            | '\uE060' -> result.Append("\uE060\uE060") |> ignore
            | 'ㄮ' -> result.Append("\uE060\uE061") |> ignore
            | 'ㆻ' -> result.Append("\uE060\uE062") |> ignore
            | c -> result.Append(c) |> ignore
        result.ToString()

// Later BMP compatibility ideographs receive implicit primary weights in
// MySQL's legacy UCA rather than the NFKC weights used by ICU.
let private foldLegacyCompatibilityIdeographs (value: string) =
    let isDistinct c =
        c = '\uFA2E' || c = '\uFA2F'
        || (c >= '\uFA6B' && c <= '\uFA6D')
        || (c >= '\uFA70' && c <= '\uFAD9')
    if not (value |> Seq.exists (fun c -> c = '\uE070' || isDistinct c)) then value
    else
        let result = StringBuilder(value.Length)
        for c in value do
            if c = '\uE070' then result.Append("\uE070\uE070") |> ignore
            elif isDistinct c then
                result.Append('\uE070').Append(char (0xE300 + int c - 0xFA00)) |> ignore
            else result.Append(c) |> ignore
        result.ToString()

// MySQL's older utf8mb4_unicode_ci and utf8mb4_general_ci collations give
// supplementary scalars a shared fallback primary weight.
let private foldLegacySupplementary (value: string) =
    if not (value |> Seq.exists (fun c -> c = '\uE090' || Char.IsHighSurrogate c)) then value
    else
        let result = StringBuilder(value.Length)
        let mutable index = 0
        while index < value.Length do
            let c = value.[index]
            if c = '\uE090' then result.Append("\uE090\uE090") |> ignore
            elif Char.IsHighSurrogate c && index + 1 < value.Length && Char.IsLowSurrogate value.[index + 1] then
                result.Append("\uE090\uE091") |> ignore
                index <- index + 1
            else result.Append(c) |> ignore
            index <- index + 1
        result.ToString()

// UCA 4.0 gives FE10–FE1F and FE24–FE2F implicit, distinct weights;
// newer MySQL weights fold many of these presentation forms.
let private foldCjkPunctuationForms modern (value: string) =
    let value =
        let isWidthForm c =
            (c >= '\uFE30' && c <= '\uFE6F') || (c >= '\uFF01' && c <= '\uFF5E')
        if modern || not (value |> Seq.exists isWidthForm) then value
        else
            // Legacy MySQL folds these presentation widths for equality and
            // ngram keys while the tokenizer retains the original characters.
            let result = StringBuilder(value.Length)
            for c in value do
                if isWidthForm c then
                    result.Append((string c).Normalize(NormalizationForm.FormKC)) |> ignore
                else
                    result.Append(c) |> ignore
            result.ToString()
    let comma = value.Replace('\uFE50', ',').Replace('\uFF0C', ',')
    if modern then
        comma.Replace('\uFE10', ',').Replace('\uFE11', '\u3001')
    else
        // Escape the marker in source text before encoding vertical forms.
        // Keep FE11's existing marker so stored ngram keys remain compatible.
        let hasImplicitWeight c =
            (c >= '\uFE10' && c <= '\uFE1F') || (c >= '\uFE24' && c <= '\uFE2F')
        if comma |> Seq.exists (fun c -> c = '\uE011' || hasImplicitWeight c) then
            let result = StringBuilder(comma.Length)
            for c in comma do
                match c with
                | '\uE011' -> result.Append("\uE011\uE011") |> ignore
                | '\uFE11' -> result.Append("\uE011\uE012") |> ignore
                | c when hasImplicitWeight c ->
                    let marker = if c = '\uFE10' then '\uE010' else char (int '\uE013' + int c - int '\uFE12')
                    result.Append('\uE011').Append(marker) |> ignore
                | c -> result.Append(c) |> ignore
            result.ToString()
        else comma

// These characters share ASCII space's primary weight in MySQL's legacy
// Unicode collations. U+1680 and the Unicode line separators do not.
let private legacyUnicodePadSpaces =
    [| ' '; '\u00a0'; '\u2000'; '\u2001'; '\u2002'; '\u2003'
       '\u2004'; '\u2005'; '\u2006'; '\u2007'; '\u2008'; '\u2009'
       '\u200a'; '\u202f'; '\u205f'; '\u3000' |]

// Legacy Unicode collations treat these separator forms as an ordinary space
// and these format controls as ignorable at the primary weight level.
let private foldLegacySpace (value: string) =
    let isSpace = function
        | '\u00a0' | '\u202f' | '\u205f' | '\u3000' -> true
        | c when c >= '\u2000' && c <= '\u200a' -> true
        | _ -> false
    if not (value |> Seq.exists (fun c -> isSpace c || c = '\u200c' || c = '\u200d' || c = '\u2060')) then value
    else
        let result = StringBuilder(value.Length)
        for c in value do
            if isSpace c then result.Append(' ') |> ignore
            elif c <> '\u200c' && c <> '\u200d' && c <> '\u2060' then result.Append(c) |> ignore
        result.ToString()

// MySQL 8.4.11 legacy UCA primary weights for ASCII 0x20–0x60 and 0x7B–0x7E.
// Lowercase letters share the uppercase weight.
let private unicode40AsciiWeights =
    [|
        0x0209; 0x0251; 0x027E; 0x02D2; 0x0E0F; 0x02D3; 0x02CF; 0x0277
        0x0288; 0x0289; 0x02C8; 0x0428; 0x022F; 0x0221; 0x025D; 0x02CC
        0x0E29; 0x0E2A; 0x0E2B; 0x0E2C; 0x0E2D; 0x0E2E; 0x0E2F; 0x0E30
        0x0E31; 0x0E32; 0x023D; 0x023A; 0x042C; 0x042D; 0x042E; 0x0255
        0x02C7; 0x0E33; 0x0E4A; 0x0E60; 0x0E6D; 0x0E8B; 0x0EB9; 0x0EC1
        0x0EE1; 0x0EFB; 0x0F10; 0x0F21; 0x0F2E; 0x0F5B; 0x0F64; 0x0F82
        0x0FA7; 0x0FB4; 0x0FC0; 0x0FEA; 0x1002; 0x101F; 0x1044; 0x1051
        0x105A; 0x105E; 0x106A; 0x028A; 0x02CE; 0x028B; 0x020F; 0x021B
        0x020C; 0x028C; 0x0430; 0x028D; 0x0433
    |]

let private unicode52AsciiWeights =
    [|
        0x020A; 0x026E; 0x02F5; 0x0362; 0x11E1; 0x0363; 0x035F; 0x02EE
        0x02FF; 0x0300; 0x0357; 0x0550; 0x0234; 0x0223; 0x0281; 0x035C
        0x1205; 0x1206; 0x1207; 0x1208; 0x1209; 0x120A; 0x120B; 0x120C
        0x120D; 0x120E; 0x0247; 0x0243; 0x0554; 0x0555; 0x0556; 0x0273
        0x0356; 0x120F; 0x1225; 0x123D; 0x1250; 0x126B; 0x12A3; 0x12B0
        0x12D3; 0x12EC; 0x1305; 0x131E; 0x1330; 0x135F; 0x136D; 0x138E
        0x13B3; 0x13C8; 0x13DA; 0x1410; 0x1433; 0x1453; 0x147B; 0x148D
        0x1497; 0x149C; 0x14AD; 0x0301; 0x035E; 0x0302; 0x0211; 0x021D
        0x020E; 0x0303; 0x0558; 0x0304; 0x055B
    |]

// MySQL 8.4.11 UCA 4.0/5.2 primary weights for CJK Symbols and Punctuation.
// U+302A–U+302F have no primary weight; U+3006 and U+303C expand to two.
let private legacyCjkSymbolWeights unicode520 codepoint =
    let single value = [ value ]
    if unicode520 then
        match codepoint with
        | 0x3000 -> single 0x020A
        | 0x3001 -> single 0x0240
        | 0x3002 -> single 0x0291
        | 0x3003 -> single 0x0374
        | 0x3004 -> single 0x1104
        | 0x3005 -> single 0x11D7
        | 0x3006 -> [ 0x2B20; 0x2B36 ]
        | 0x3007 -> single 0x1205
        | value when value >= 0x3008 && value <= 0x3011 -> single (0x033D + value - 0x3008)
        | 0x3012 -> single 0x1105
        | 0x3013 -> single 0x1106
        | value when value >= 0x3014 && value <= 0x301B -> single (0x0347 + value - 0x3014)
        | 0x301C -> single 0x0230
        | value when value >= 0x301D && value <= 0x301F -> single (0x02FA + value - 0x301D)
        | 0x3020 -> single 0x1107
        | value when value >= 0x3021 && value <= 0x3029 -> single (0x1206 + value - 0x3021)
        | value when value >= 0x302A && value <= 0x302F -> []
        | 0x3030 -> single 0x0231
        | value when value >= 0x3031 && value <= 0x3035 -> single (0x11D9 + (value - 0x3031) / 2)
        | 0x3036 -> single 0x1105
        | 0x3037 -> single 0x1108
        | 0x3038 -> [ 0xFB40; 0xD341 ]
        | 0x3039 -> [ 0xFB40; 0xD344 ]
        | 0x303A -> [ 0xFB40; 0xD345 ]
        | 0x303B -> single 0x11D8
        | 0x303C -> [ 0x2B33; 0x2B21 ]
        | 0x303D -> single 0x0375
        | 0x303E -> single 0x1109
        | 0x303F -> single 0x110A
        | _ -> []
    else
        match codepoint with
        | 0x3000 -> single 0x0209
        | 0x3001 -> single 0x0237
        | 0x3002 -> single 0x0266
        | 0x3003 -> single 0x02E2
        | 0x3004 -> single 0x0DBB
        | 0x3005 -> single 0x0E05
        | 0x3006 -> [ 0x1E5D; 0x1E73 ]
        | 0x3007 -> single 0x0E29
        | value when value >= 0x3008 && value <= 0x3011 -> single (0x02AE + value - 0x3008)
        | 0x3012 -> single 0x0DBC
        | 0x3013 -> single 0x0DBD
        | value when value >= 0x3014 && value <= 0x301B -> single (0x02B8 + value - 0x3014)
        | 0x301C -> single 0x022B
        | value when value >= 0x301D && value <= 0x301F -> single (0x0283 + value - 0x301D)
        | 0x3020 -> single 0x0DBE
        | value when value >= 0x3021 && value <= 0x3029 -> single (0x0E2A + value - 0x3021)
        | value when value >= 0x302A && value <= 0x302F -> []
        | 0x3030 -> single 0x022C
        | value when value >= 0x3031 && value <= 0x3035 -> single (0x0E07 + (value - 0x3031) / 2)
        | 0x3036 -> single 0x0DBC
        | 0x3037 -> single 0x0DBF
        | 0x3038 -> [ 0xFB40; 0xD341 ]
        | 0x3039 -> [ 0xFB40; 0xD344 ]
        | 0x303A -> [ 0xFB40; 0xD345 ]
        | 0x303B -> single 0x0E06
        | 0x303C -> [ 0x1E70; 0x1E5E ]
        | 0x303D -> single 0x02E3
        | 0x303E -> single 0x0DC0
        | 0x303F -> single 0x0DC1
        | _ -> []

// MySQL 8.4.11 UCA primary weights for the full Hiragana block. A zero is
// ignorable; values over 0xFFFF encode two consecutive primary weights.
let private unicode40HiraganaWeights =
    [|
        0xFBC0B040u; 0x1E52u; 0x1E52u; 0x1E53u; 0x1E53u; 0x1E54u; 0x1E54u; 0x1E55u
        0x1E55u; 0x1E56u; 0x1E56u; 0x1E57u; 0x1E57u; 0x1E58u; 0x1E58u; 0x1E59u
        0x1E59u; 0x1E5Au; 0x1E5Au; 0x1E5Bu; 0x1E5Bu; 0x1E5Cu; 0x1E5Cu; 0x1E5Du
        0x1E5Du; 0x1E5Eu; 0x1E5Eu; 0x1E5Fu; 0x1E5Fu; 0x1E60u; 0x1E60u; 0x1E61u
        0x1E61u; 0x1E62u; 0x1E62u; 0x1E63u; 0x1E63u; 0x1E63u; 0x1E64u; 0x1E64u
        0x1E65u; 0x1E65u; 0x1E66u; 0x1E67u; 0x1E68u; 0x1E69u; 0x1E6Au; 0x1E6Bu
        0x1E6Bu; 0x1E6Bu; 0x1E6Cu; 0x1E6Cu; 0x1E6Cu; 0x1E6Du; 0x1E6Du; 0x1E6Du
        0x1E6Eu; 0x1E6Eu; 0x1E6Eu; 0x1E6Fu; 0x1E6Fu; 0x1E6Fu; 0x1E70u; 0x1E71u
        0x1E72u; 0x1E73u; 0x1E74u; 0x1E75u; 0x1E75u; 0x1E76u; 0x1E76u; 0x1E77u
        0x1E77u; 0x1E78u; 0x1E79u; 0x1E7Au; 0x1E7Bu; 0x1E7Cu; 0x1E7Du; 0x1E7Du
        0x1E7Eu; 0x1E7Fu; 0x1E80u; 0x1E81u; 0x1E54u; 0x1E57u; 0x1E5Au; 0xFBC0B097u
        0xFBC0B098u; 0x0000u; 0x0000u; 0x021Eu; 0x021Fu; 0x0E0Au; 0x0E0Au; 0x1E771E79u
    |]

let private unicode52HiraganaWeights =
    [|
        0xFBC0B040u; 0x2B15u; 0x2B15u; 0x2B16u; 0x2B16u; 0x2B17u; 0x2B17u; 0x2B18u
        0x2B18u; 0x2B19u; 0x2B19u; 0x2B1Au; 0x2B1Au; 0x2B1Bu; 0x2B1Bu; 0x2B1Cu
        0x2B1Cu; 0x2B1Du; 0x2B1Du; 0x2B1Eu; 0x2B1Eu; 0x2B1Fu; 0x2B1Fu; 0x2B20u
        0x2B20u; 0x2B21u; 0x2B21u; 0x2B22u; 0x2B22u; 0x2B23u; 0x2B23u; 0x2B24u
        0x2B24u; 0x2B25u; 0x2B25u; 0x2B26u; 0x2B26u; 0x2B26u; 0x2B27u; 0x2B27u
        0x2B28u; 0x2B28u; 0x2B29u; 0x2B2Au; 0x2B2Bu; 0x2B2Cu; 0x2B2Du; 0x2B2Eu
        0x2B2Eu; 0x2B2Eu; 0x2B2Fu; 0x2B2Fu; 0x2B2Fu; 0x2B30u; 0x2B30u; 0x2B30u
        0x2B31u; 0x2B31u; 0x2B31u; 0x2B32u; 0x2B32u; 0x2B32u; 0x2B33u; 0x2B34u
        0x2B35u; 0x2B36u; 0x2B37u; 0x2B38u; 0x2B38u; 0x2B39u; 0x2B39u; 0x2B3Au
        0x2B3Au; 0x2B3Bu; 0x2B3Cu; 0x2B3Du; 0x2B3Eu; 0x2B3Fu; 0x2B40u; 0x2B40u
        0x2B41u; 0x2B42u; 0x2B43u; 0x2B44u; 0x2B17u; 0x2B1Au; 0x2B1Du; 0xFBC0B097u
        0xFBC0B098u; 0x0000u; 0x0000u; 0x0220u; 0x0221u; 0x11DCu; 0x11DCu; 0x2B3A2B3Cu
    |]

let private legacyKanaWeights unicode520 codepoint =
    let weights = if unicode520 then unicode52HiraganaWeights else unicode40HiraganaWeights
    let encoded =
        if codepoint <= 0x309F then weights[codepoint - 0x3040]
        elif codepoint <= 0x30F6 then
            if codepoint = 0x30A0 then (if unicode520 then 0x0232u else 0x022Du)
            else weights[codepoint - 0x30A0]
        else
            match codepoint, unicode520 with
            | 0x30F7, false -> 0x1E7Du | 0x30F7, true -> 0x2B40u
            | 0x30F8, false -> 0x1E7Eu | 0x30F8, true -> 0x2B41u
            | 0x30F9, false -> 0x1E7Fu | 0x30F9, true -> 0x2B42u
            | 0x30FA, false -> 0x1E80u | 0x30FA, true -> 0x2B43u
            | 0x30FB, false -> 0x022Eu | 0x30FB, true -> 0x0233u
            | 0x30FC, false -> 0x0E0Bu | 0x30FC, true -> 0x11DDu
            | 0x30FD, false | 0x30FE, false -> 0x0E0Cu
            | 0x30FD, true | 0x30FE, true -> 0x11DEu
            | 0x30FF, false -> 0x1E5B1E65u | 0x30FF, true -> 0x2B1E2B28u
            | _ -> 0u
    if encoded = 0u then []
    elif encoded <= 0xFFFFu then [ int encoded ]
    else [ int (encoded >>> 16); int (encoded &&& 0xFFFFu) ]

// MySQL 8.4.11's legacy UCA weights for U+3105–U+312D. The surrounding
// unassigned Bopomofo positions use the legacy implicit two-weight form.
let private unicode40BopomofoWeights =
    [|
        0x1E82u; 0x1E83u; 0x1E84u; 0x1E85u; 0x1E87u; 0x1E88u; 0x1E89u; 0x1E8Au
        0x1E8Bu; 0x1E8Cu; 0x1E8Fu; 0x1E90u; 0x1E91u; 0x1E92u; 0x1E94u; 0x1E95u
        0x1E96u; 0x1E97u; 0x1E98u; 0x1E99u; 0x1E9Au; 0x1E9Bu; 0x1E9Cu; 0x1E9Eu
        0x1E9Fu; 0x1EA1u; 0x1EA2u; 0x1EA3u; 0x1EA4u; 0x1EA5u; 0x1EA6u; 0x1EA7u
        0x1EA9u; 0x1EADu; 0x1EAEu; 0x1EAFu; 0x1EB0u; 0x1E86u; 0x1E8Du; 0x1E93u
        0xFBC0B12Du
    |]

let private unicode52BopomofoWeights =
    [|
        0x2B45u; 0x2B46u; 0x2B47u; 0x2B48u; 0x2B4Au; 0x2B4Bu; 0x2B4Cu; 0x2B4Du
        0x2B4Eu; 0x2B4Fu; 0x2B52u; 0x2B53u; 0x2B54u; 0x2B55u; 0x2B57u; 0x2B58u
        0x2B59u; 0x2B5Au; 0x2B5Bu; 0x2B5Cu; 0x2B5Du; 0x2B5Eu; 0x2B5Fu; 0x2B61u
        0x2B62u; 0x2B64u; 0x2B65u; 0x2B66u; 0x2B67u; 0x2B68u; 0x2B69u; 0x2B6Au
        0x2B6Cu; 0x2B70u; 0x2B71u; 0x2B72u; 0x2B73u; 0x2B49u; 0x2B50u; 0x2B56u
        0x2B74u
    |]

let private legacyBopomofoWeights unicode520 codepoint =
    let encoded =
        if codepoint < 0x3105 || codepoint >= 0x312E then
            0xFBC08000u + uint32 codepoint
        elif unicode520 then
            unicode52BopomofoWeights[codepoint - 0x3105]
        else
            unicode40BopomofoWeights[codepoint - 0x3105]
    if encoded <= 0xFFFFu then [ int encoded ]
    else [ int (encoded >>> 16); int (encoded &&& 0xFFFFu) ]

// MySQL 8.4.11 legacy UCA primary weights for Hangul Compatibility Jamo.
// The unassigned positions at either end use implicit two-weight values.
let private unicode40CompatibilityJamoWeights =
    [|
        0x1D62u; 0x1D63u; 0x1E02u; 0x1D64u; 0x1E04u; 0x1E05u; 0x1D65u; 0x1D66u
        0x1D67u; 0x1E08u; 0x1E09u; 0x1E0Au; 0x1E0Bu; 0x1E0Cu; 0x1E0Du; 0x1D7Cu
        0x1D68u; 0x1D69u; 0x1D6Au; 0x1D83u; 0x1D6Bu; 0x1D6Cu; 0x1D6Du; 0x1D6Eu
        0x1D6Fu; 0x1D70u; 0x1D71u; 0x1D72u; 0x1D73u; 0x1D74u; 0x1DBEu; 0x1DBFu
        0x1DC0u; 0x1DC1u; 0x1DC2u; 0x1DC3u; 0x1DC4u; 0x1DC5u; 0x1DC6u; 0x1DC7u
        0x1DC8u; 0x1DC9u; 0x1DCAu; 0x1DCBu; 0x1DCCu; 0x1DCDu; 0x1DCEu; 0x1DCFu
        0x1DD0u; 0x1DD1u; 0x1DD2u; 0x1DBDu; 0x1D76u; 0x1D77u; 0x1E1Fu; 0x1E20u
        0x1E24u; 0x1E26u; 0x1E2Bu; 0x1E2Fu; 0x1E31u; 0x1D7Eu; 0x1E35u; 0x1E37u
        0x1D7Fu; 0x1D80u; 0x1D82u; 0x1D84u; 0x1D85u; 0x1D89u; 0x1D8Bu; 0x1D8Du
        0x1D8Eu; 0x1D8Fu; 0x1D90u; 0x1D91u; 0x1D94u; 0x1D98u; 0x1DA2u; 0x1DA9u
        0x1DAEu; 0x1E49u; 0x1E4Au; 0x1DB9u; 0x1DBAu; 0x1DBBu; 0x1DE1u; 0x1DE2u
        0x1DE5u; 0x1DEEu; 0x1DEFu; 0x1DF1u; 0x1DFBu; 0x1DFEu
    |]

let private unicode52CompatibilityJamoWeights =
    [|
        0x29B0u; 0x29B1u; 0x2A8Eu; 0x29B2u; 0x2A90u; 0x2A91u; 0x29B3u; 0x29B4u
        0x29B5u; 0x2A94u; 0x2A95u; 0x2A96u; 0x2A97u; 0x2A98u; 0x2A99u; 0x29CAu
        0x29B6u; 0x29B7u; 0x29B8u; 0x29D1u; 0x29B9u; 0x29BAu; 0x29BBu; 0x29BCu
        0x29BDu; 0x29BEu; 0x29BFu; 0x29C0u; 0x29C1u; 0x29C2u; 0x2A2Eu; 0x2A2Fu
        0x2A30u; 0x2A31u; 0x2A32u; 0x2A33u; 0x2A34u; 0x2A35u; 0x2A36u; 0x2A37u
        0x2A38u; 0x2A39u; 0x2A3Au; 0x2A3Bu; 0x2A3Cu; 0x2A3Du; 0x2A3Eu; 0x2A3Fu
        0x2A40u; 0x2A41u; 0x2A42u; 0x2A2Du; 0x29C4u; 0x29C5u; 0x2AABu; 0x2AACu
        0x2AB0u; 0x2AB2u; 0x2AB7u; 0x2ABBu; 0x2ABDu; 0x29CCu; 0x2AC1u; 0x2AC3u
        0x29CDu; 0x29CEu; 0x29D0u; 0x29D2u; 0x29D3u; 0x29D7u; 0x29D9u; 0x29DBu
        0x29DCu; 0x29DDu; 0x29DEu; 0x29DFu; 0x29E2u; 0x29E6u; 0x29F0u; 0x29F7u
        0x29FCu; 0x2AD5u; 0x2AD6u; 0x2A07u; 0x2A08u; 0x2A09u; 0x2A51u; 0x2A52u
        0x2A55u; 0x2A5Eu; 0x2A5Fu; 0x2A61u; 0x2A6Bu; 0x2A6Eu
    |]

let private legacyCompatibilityJamoWeights unicode520 codepoint =
    if codepoint = 0x3130 || codepoint = 0x318F then
        [ 0xFBC0; 0x8000 + codepoint ]
    elif unicode520 then
        [ int unicode52CompatibilityJamoWeights[codepoint - 0x3131] ]
    else
        [ int unicode40CompatibilityJamoWeights[codepoint - 0x3131] ]

let private enclosedHangulBaseJamo =
    [| 0x3131; 0x3134; 0x3137; 0x3139; 0x3141; 0x3142; 0x3145
       0x3147; 0x3148; 0x314A; 0x314B; 0x314C; 0x314D; 0x314E |]

// Enclosed Hangul letters reuse the corresponding compatibility-Jamo
// primaries. Parenthesized forms add the vowel suffix; the last four are
// independent legacy UCA sequences.
let private legacyEnclosedHangulWeights unicode520 codepoint =
    if codepoint <= 0x327B then
        let offset = codepoint - 0x3260
        let weights = legacyCompatibilityJamoWeights unicode520 enclosedHangulBaseJamo[offset % enclosedHangulBaseJamo.Length]
        if offset < enclosedHangulBaseJamo.Length then weights
        else weights @ [ if unicode520 then 0x2A2E else 0x1DBE ]
    else
        match codepoint, unicode520 with
        | 0x327C, false -> [ 0x1D70; 0x1DBE; 0x1E0F; 0x1D62; 0x1DC6 ]
        | 0x327C, true -> [ 0x29BE; 0x2A2E; 0x2A9B; 0x29B0; 0x2A36 ]
        | 0x327D, false -> [ 0x1D6E; 0x1DCB; 0x1D6D; 0x1DD1 ]
        | 0x327D, true -> [ 0x29BC; 0x2A3B; 0x29BB; 0x2A41 ]
        | 0x327E, false -> [ 0xFBC0; 0xB27E ]
        | 0x327E, true -> [ 0x29BB; 0x2A3B ]
        | 0x327F, false -> [ 0x0DC4 ]
        | _ -> [ 0x110D ]

// Parenthesized Hangul Jamo share the enclosed-letter primaries but carry
// distinct prefix and closing weights in the two legacy UCA versions.
let private legacyParenthesizedHangulWeights unicode520 codepoint =
    let inner =
        if codepoint <= 0x321B then
            let offset = codepoint - 0x3200
            let primary = legacyCompatibilityJamoWeights unicode520 enclosedHangulBaseJamo[offset % enclosedHangulBaseJamo.Length]
            if offset < enclosedHangulBaseJamo.Length then primary
            else primary @ [ if unicode520 then 0x2A2E else 0x1DBE ]
        else
            match codepoint, unicode520 with
            | 0x321C, false -> [ 0x1D6E; 0x1DCB ]
            | 0x321C, true -> [ 0x29BC; 0x2A3B ]
            | 0x321D, false -> [ 0x1D6D; 0x1DC6; 0x1D6E; 0x1DC2; 0x1E03 ]
            | 0x321D, true -> [ 0x29BB; 0x2A36; 0x29BC; 0x2A32; 0x2A8F ]
            | 0x321E, false -> [ 0x1D6D; 0x1DC6; 0x1D74; 0x1DCB ]
            | _ -> [ 0x29BB; 0x2A36; 0x29C2; 0x2A3B ]
    if unicode520 then [ 0x02FF ] @ inner @ [ 0x0300 ]
    else [ 0x0288 ] @ inner @ [ 0x0289 ]

let private makeCollation (name: string) (spec: Spec) : Collation =
    let ci = compareInfoFor spec.Locale
    let trim (s: string) =
        if not spec.PadSpace then s
        elif name = "utf8mb4_unicode_ci" || name = "utf8mb4_unicode_520_ci" then
            // MySQL gives these Unicode spaces the same primary weight as
            // ASCII space, so PAD SPACE removes them at the end of a key.
            s.TrimEnd(legacyUnicodePadSpaces)
        else s.TrimEnd(' ')
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
        match name with
        | "utf8mb4_ja_0900_as_cs_ks" -> value |> markJapaneseHiragana |> foldJapaneseCompatibility
        | "utf8mb4_general_ci" ->
            value.Replace("ß", "s").Replace("ẞ", "s")
            |> foldLegacySupplementary |> foldGeneralUnicodeJamo
            |> foldGeneralSyntheticWeights
        | "utf8mb4_0900_ai_ci"
        | "utf8mb4_0900_as_ci" ->
            value |> foldCjkPunctuationForms true |> foldKanaVariants |> foldSmallKana
            |> foldUnicodeQuoteForms |> foldUnicode520MongolianSeparator |> foldLegacyUnicodeJamo
            |> foldSupplementaryKanaImplicit false
        | "utf8mb4_unicode_520_ci" ->
            value |> foldLegacySpace |> foldCjkPunctuationForms true |> foldLegacyBopomofoForms
            |> foldKanaVariants |> foldSmallKana |> foldUnicodeQuoteForms
            |> foldUnicode520MongolianSeparator |> foldUnicode520CombiningHalfMarks
            |> foldLegacyExtendedCombiningMarks true
            |> foldLegacyUnicodeJamo |> foldSupplementaryKanaImplicit true
            |> foldUnicode520EnclosedImplicit
        | "utf8mb4_unicode_ci" ->
            value |> foldLegacySpace |> foldCjkPunctuationForms false |> foldLegacyEnclosedCjk |> foldLegacyLetterlikeForms
                  |> foldLegacyKanaForms |> foldLegacyBopomofoForms
                  |> foldLegacyCompatibilityIdeographs
                  |> foldLegacySupplementary
                  |> foldLegacyExtendedCombiningMarks false
                  |> foldKanaVariants |> foldSmallKana |> foldUnicodeQuoteForms
                  |> foldLegacyUnicodeJamo
        | _ -> value

    let primaryText (s: string) = s |> foldText |> trim
    let substringFold =
        if spec.Fold.HasFlag CompareOptions.IgnoreCase then
            CompareOptions.IgnoreCase
        else
            CompareOptions.None

    let substringText (value: string) =
        let expanded =
            if name = "utf8mb4_general_ci" then
                value
            else
                substringExpansions |> List.fold (fun text (source, target) -> text.Replace(source, target)) value

        expanded.Normalize(NormalizationForm.FormD)

    let binaryWeight (s: string) =
        if name = "utf8mb3_bin" || name = "utf8mb4_bin" then
            s.EnumerateRunes()
            |> Seq.collect (fun rune ->
                let value = rune.Value
                [ byte (value >>> 16); byte (value >>> 8); byte value ])
            |> Array.ofSeq
        else
            Charset.tryEncodeStrict charset s |> Option.defaultWith (fun () -> binaryBytesWithoutPadding s)

    let legacyWeight (s: string) =
        let general = name = "utf8mb4_general_ci" || name = "utf8mb3_general_ci"
        let asciiWeights =
            if name = "utf8mb4_unicode_520_ci" then unicode52AsciiWeights
            else unicode40AsciiWeights
        let isLegacy =
            general || name = "utf8mb4_unicode_ci"
            || name = "utf8mb4_unicode_520_ci"
            || name = "utf8mb3_unicode_ci"
        if not isLegacy || s.Length = 0 then None
        else
            let bytes = ResizeArray<byte>()
            let addWeight weight =
                bytes.Add(byte (weight >>> 8))
                bytes.Add(byte weight)
            let addImplicit lead codepoint =
                addWeight (lead + (codepoint >>> 15))
                addWeight (0x8000 ||| (codepoint &&& 0x7FFF))
            let compatibilityTarget (rune: Rune) =
                rune.ToString().Normalize(NormalizationForm.FormKC).EnumerateRunes()
                |> Seq.head
                |> fun value -> value.Value
            let mutable supported = true
            for rune in s.EnumerateRunes() do
                let codepoint = rune.Value
                let supplementaryIdeograph =
                    (codepoint >= 0x20000 && codepoint <= 0x2EE5F)
                    || (codepoint >= 0x30000 && codepoint <= 0x323AF)
                let supplementaryCompatibility = codepoint >= 0x2F800 && codepoint <= 0x2FA1F
                let bmpCompatibility = codepoint >= 0xF900 && codepoint <= 0xFAFF
                // UCA 4.0/5.2 assigns ideographs through these boundaries;
                // later scalars in the same BMP blocks use the unassigned lead.
                let lead =
                    if codepoint >= 0x3400 && codepoint <= 0x4DBF then
                        Some(if codepoint <= 0x4DB5 then 0xFB80 else 0xFBC0)
                    elif codepoint >= 0x4E00 && codepoint <= 0x9FFF then
                        Some(if codepoint <= 0x9FA5 then 0xFB40 else 0xFBC0)
                    else
                        None
                match lead with
                | None when bmpCompatibility ->
                    if general then
                        addWeight codepoint
                    else
                        let mapped = compatibilityTarget rune
                        let useMapping =
                            mapped <> codepoint
                            && (if name = "utf8mb4_unicode_520_ci" then
                                    codepoint <> 0xFA2E && codepoint <> 0xFA2F
                                else
                                    codepoint < 0xFA2E || (codepoint >= 0xFA30 && codepoint <= 0xFA6A))
                        let target = if useMapping then mapped else codepoint
                        let implicitLead =
                            if useMapping then
                                if target >= 0x10000 || target <= 0x4DB5 then 0xFB80
                                elif target <= 0x9FA5 then 0xFB40
                                else 0xFBC0
                            elif codepoint < 0xFA2E then 0xFB40
                            else 0xFBC0
                        addImplicit implicitLead target
                | None when supplementaryCompatibility && name.StartsWith("utf8mb4_") ->
                    let weights =
                        if name = "utf8mb4_unicode_520_ci" then
                            let mapped = compatibilityTarget rune
                            let implicitLead =
                                if mapped = codepoint then 0xFBC0
                                elif mapped >= 0x10000 then 0xFB80
                                elif mapped <= 0x4DB5 then 0xFB80
                                elif mapped <= 0x9FA5 then 0xFB40
                                else 0xFBC0
                            [ implicitLead + (mapped >>> 15); 0x8000 ||| (mapped &&& 0x7FFF) ]
                        else [ 0xFFFD ]
                    for weight in weights do addWeight weight
                | None when supplementaryIdeograph && name.StartsWith("utf8mb4_") ->
                    let weights =
                        if name = "utf8mb4_unicode_520_ci" then
                            [ 0xFBC0 + (codepoint >>> 15); 0x8000 ||| (codepoint &&& 0x7FFF) ]
                        else [ 0xFFFD ]
                    for weight in weights do addWeight weight
                | None when codepoint >= 0x3000 && codepoint <= 0x303F ->
                    if general then addWeight codepoint
                    else
                        for weight in legacyCjkSymbolWeights (name = "utf8mb4_unicode_520_ci") codepoint do
                            addWeight weight
                | None when codepoint >= 0x3040 && codepoint <= 0x30FF ->
                    if general then addWeight codepoint
                    else
                        for weight in legacyKanaWeights (name = "utf8mb4_unicode_520_ci") codepoint do
                            addWeight weight
                | None when codepoint >= 0x3100 && codepoint <= 0x312F ->
                    if general then addWeight codepoint
                    else
                        for weight in legacyBopomofoWeights (name = "utf8mb4_unicode_520_ci") codepoint do
                            addWeight weight
                | None when codepoint >= 0x3130 && codepoint <= 0x318F ->
                    if general then addWeight codepoint
                    else
                        for weight in legacyCompatibilityJamoWeights (name = "utf8mb4_unicode_520_ci") codepoint do
                            addWeight weight
                | None when codepoint >= 0x3200 && codepoint <= 0x321F ->
                    if general then addWeight codepoint
                    elif codepoint = 0x321F then
                        for weight in [ 0xFBC0; 0xB21F ] do addWeight weight
                    else
                        for weight in legacyParenthesizedHangulWeights (name = "utf8mb4_unicode_520_ci") codepoint do
                            addWeight weight
                | None when codepoint >= 0x3260 && codepoint <= 0x327F ->
                    if general then addWeight codepoint
                    else
                        for weight in legacyEnclosedHangulWeights (name = "utf8mb4_unicode_520_ci") codepoint do
                            addWeight weight
                | None when codepoint >= 0x20 && codepoint <= 0x7E ->
                    let weight =
                        let uppercase =
                            if codepoint >= int 'a' && codepoint <= int 'z' then codepoint - 0x20
                            else codepoint
                        if general then uppercase
                        elif uppercase <= 0x60 then asciiWeights[uppercase - 0x20]
                        else asciiWeights[uppercase - 0x7B + 65]
                    addWeight weight
                | None -> supported <- false
                | Some _ when general ->
                    addWeight codepoint
                | Some lead ->
                    addImplicit lead codepoint
            if supported then Some(bytes.ToArray()) else None

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
                legacyWeight s
                |> Option.defaultWith (fun () -> ci.GetSortKey(foldText s, spec.Fold).KeyData)
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
      IsSubstringPrefix =
        if spec.ByteOrder then
            binaryPrefix
        else
            fun value prefix -> ci.IsPrefix(substringText value, substringText prefix, substringFold)
      FindSubstringAscii =
        if spec.ByteOrder then
            fun value needle offset -> value.IndexOf(needle, offset, StringComparison.Ordinal)
        else
            fun value needle offset -> ci.IndexOf(value, needle, offset, substringFold)
      PadSpace = spec.PadSpace
      EqualityIsOrderEquivalence =
        not spec.PadSpace
        && (spec.ByteOrder || spec.Fold = CompareOptions.None) }

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
    [ "armscii8_general_ci", 32, 1, "armscii8_bin", 64, Some "hy-AM"
      "big5_chinese_ci", 1, 1, "big5_bin", 84, Some "zh-Hant-TW"
      "cp1250_general_ci", 26, 1, "cp1250_bin", 66, None
      "cp1251_general_ci", 51, 1, "cp1251_bin", 50, Some "ru-RU"
      "cp1256_general_ci", 57, 1, "cp1256_bin", 67, Some "ar-SA"
      "cp1257_general_ci", 59, 1, "cp1257_bin", 58, None
      "cp850_general_ci", 4, 1, "cp850_bin", 80, None
      "cp852_general_ci", 40, 1, "cp852_bin", 81, None
      "cp866_general_ci", 36, 1, "cp866_bin", 68, Some "ru-RU"
      "cp932_japanese_ci", 95, 1, "cp932_bin", 96, Some "ja-JP"
      "dec8_swedish_ci", 3, 1, "dec8_bin", 69, Some "sv-SE"
      "euckr_korean_ci", 19, 1, "euckr_bin", 85, Some "ko-KR"
      "gb18030_chinese_ci", 248, 2, "gb18030_bin", 249, Some "zh-Hans-CN"
      "gb2312_chinese_ci", 24, 1, "gb2312_bin", 86, Some "zh-Hans-CN"
      "gbk_chinese_ci", 28, 1, "gbk_bin", 87, Some "zh-Hans-CN"
      "geostd8_general_ci", 92, 1, "geostd8_bin", 93, Some "ka-GE"
      "greek_general_ci", 25, 1, "greek_bin", 70, Some "el-GR"
      "hebrew_general_ci", 16, 1, "hebrew_bin", 71, Some "he-IL"
      "hp8_english_ci", 6, 1, "hp8_bin", 72, None
      "keybcs2_general_ci", 37, 1, "keybcs2_bin", 73, Some "cs-CZ"
      "koi8r_general_ci", 7, 1, "koi8r_bin", 74, Some "ru-RU"
      "koi8u_general_ci", 22, 1, "koi8u_bin", 75, Some "uk-UA"
      "latin2_general_ci", 9, 1, "latin2_bin", 77, None
      "latin5_turkish_ci", 30, 1, "latin5_bin", 78, Some "tr-TR"
      "latin7_general_ci", 41, 1, "latin7_bin", 79, None
      "macce_general_ci", 38, 1, "macce_bin", 43, None
      "macroman_general_ci", 39, 1, "macroman_bin", 53, None
      "sjis_japanese_ci", 13, 1, "sjis_bin", 88, Some "ja-JP"
      "swe7_swedish_ci", 10, 1, "swe7_bin", 82, Some "sv-SE"
      "tis620_thai_ci", 18, 4, "tis620_bin", 89, Some "th-TH"
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

/// The store-level fallback used when no session, column, or explicit
/// collation takes precedence.
let defaultCollation = Map.find "utf8mb4_0900_ai_ci" registry
let metadataIdentifierCollation = Map.find "utf8mb3_tolower_ci" registry
let binaryId = 63us

/// Resolves an optional collation name through the store-level fallback.
let findOrDefault name =
    name |> Option.bind tryFind |> Option.defaultValue defaultCollation
