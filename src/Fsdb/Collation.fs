/// String collation semantics — the single home for how fsdb compares and
/// sorts text. Everything that needs string semantics delegates here;
/// nothing re-derives its own rules per call site.
///
/// The one collation implemented is MySQL 8's default, utf8mb4_0900_ai_ci
/// (UCA 9.0 family): accent- and case-insensitive *equality* (primary
/// weights only — 'åge' = 'age', 'ß' = 'ss', 'æ' = 'ae', 'ǅ' = 'dž', all
/// MySQL-verified), NO PAD (trailing spaces significant — see the history
/// behind `Value.compareStrings`), with secondary/tertiary weights only
/// ever breaking `ORDER BY` ties among primary-equal strings.
///
/// Implementation is ICU collation via .NET's `CompareInfo` — the same
/// weight family UCA defines, so equality (what indexes, unique keys, and
/// joins depend on) matches MySQL exactly.
///
/// ponytails:
///  - the exact tie-break *order* among accent variants under ORDER BY may
///    differ from MySQL's own UCA 9.0 weight table, because the host ICU's
///    CLDR version differs (Apple ICU vs CLDR 30); equality never does.
///  - `COLLATE` clauses, per-column collations, and the language-specific
///    (utf8mb4_da_0900_ai_ci / _nb_ / _sv_ / ...) collations are
///    unsupported; `Collation` is the seam they'd slot into as data.
///  - LIKE folds per character and never expands: 'æ' LIKE 'ae' is false
///    while 'æ' = 'ae' is true (MySQL-verified) — see `Executor.likeMatch`.
///  - REGEXP stays accent-sensitive (a regex engine doesn't fold weights);
///    documented at `Executor.regexpOp`.
///  - identifiers (column/table names) keep their ordinal comparison —
///    MySQL's identifier collation is a separate concern.
module Fsdb.Collation

open System
open System.Globalization

/// One collation = the three behaviors the engine consumes. Everything
/// else delegates; nothing re-derives its own rules.
type Collation =
    { Name: string
      /// Full order for `ORDER BY`/`<`/`>`: primary, then secondary, then
      /// tertiary weights — accents and case break ties among
      /// primary-equal strings, exactly as MySQL's ai_ci sorts them.
      Compare: string -> string -> int
      /// The *folded* order: accent/case-only differences compare equal
      /// (0). Drives `Value.compare`, so equality, hash-join keys, and
      /// unique lookups all inherit ai_ci folding.
      ComparePrimary: string -> string -> int
      /// Equality + unique-key semantics: primary weights only for `_ai_ci`
      /// ('åge' = 'age' — accents and case never break equality).
      Equals: string -> string -> bool
      /// A canonical index key: `Equals a b` iff `KeyOf a = KeyOf b` — the
      /// unique index and its point lookups both key on this.
      KeyOf: string -> string
      /// Per-character LIKE folding, explicitly without expansions.
      CharEquals: char -> char -> bool }

let private icu = CultureInfo.InvariantCulture.CompareInfo

let private aiCi = CompareOptions.IgnoreCase ||| CompareOptions.IgnoreNonSpace

/// `CompareInfo.GetSortKey` under the same options the comparison uses —
/// ICU guarantees two strings get the same sort key iff they compare equal,
/// which is exactly the contract the unique index needs from `KeyOf`.
let private keyOf (s: string) : string =
    Convert.ToHexString(icu.GetSortKey(s, aiCi).KeyData)

let private charEquals (a: char) (b: char) : bool = icu.Compare(string a, string b, aiCi) = 0

let utf8mb4_0900_ai_ci : Collation =
    { Name = "utf8mb4_0900_ai_ci"
      // Full ICU collation (primary/secondary/tertiary) — the `Ignore*`
      // options would collapse case/accent ties to 0 and leave ORDER BY
      // with no deterministic tie-break, which MySQL's ai_ci does not do.
      Compare = fun a b -> icu.Compare(a, b, CompareOptions.None)
      ComparePrimary = fun a b -> icu.Compare(a, b, aiCi)
      Equals = fun a b -> icu.Compare(a, b, aiCi) = 0
      KeyOf = keyOf
      CharEquals = charEquals }

/// The engine's one active collation — a `Store`-level default today, the
/// seam a per-column `COLLATE` would later replace with.
let defaultCollation = utf8mb4_0900_ai_ci
