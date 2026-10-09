# REGEXP POSIX character classes

MySQL 8.4.11 was probed on the disposable native server. `REGEXP_LIKE` with
match type `c` treats `[[:lower:]]` and `[[:upper:]]` as Unicode lowercase and
uppercase letters: ASCII `a`/`A` and Greek `α`/`Α` have the corresponding
results. Match type `i` admits both cases. `[[:xdigit:]]` accepts ASCII A–F,
a–f, and Unicode decimal digits (including `５`), but not fullwidth `Ｇ` or
`g`. `[[:blank:]]` accepts space, tab, and nonbreaking space, but not newline.

Further probes established the missing `ascii`, `cntrl`, `punct`, `graph`, and
`print` classes over representative Unicode general categories. The existing
`word` class now includes combining marks, connector punctuation, and ZWNJ/ZWJ;
`alpha` and `alnum` include letter-number characters such as `Ⅳ`. The native
matrix distinguishes a control from a blank, a space from a graph character,
and a symbol from punctuation.

MySQL also accepts negated POSIX classes such as `[[:^alpha:]]`. Negation
includes newline when the positive class excludes it, including for `graph`
and `print`. The compiler derives negated translations from the same positive
class table, so a class's two forms cannot drift apart.

Positive members also combine with literals and other members within one
bracket expression: `[a[:digit:]]`, `[[:alpha:][:digit:]]`, and their quantified
forms match the native oracle. Normalization and invalid-class validation now
share one scan. MySQL rejects `[a[:bogus:]]` with 3685, which the regression
checks. Mixed negated members such as `[x[:^alpha:]]` use a one-character
alternation; an outer `^` complements the union. Quantifiers still apply to
the whole bracket expression, and unknown members still return 3685.

The compiler also translates ICU `\h`/`\H` and `\v`/`\V` escapes, alone and
in audited bracket expressions. Native MySQL puts space, tab, and nonbreaking
space in the horizontal class; newline, carriage return, vertical tab, form
feed, NEL, and Unicode line/paragraph separators are vertical. The uppercase
escapes are their complements. .NET rejects `\h`, `\H`, and `\V` without
translation and treats `\v` as vertical tab alone.

MySQL's `\R` matches one line break, consuming CRLF as a single match. Native
probes covered CRLF, CR, LF, vertical tab, form feed, NEL, and Unicode
line/paragraph separators through `REGEXP_SUBSTR`, `REGEXP_INSTR`, and
`REGEXP_REPLACE`. The compiler now translates `\R` outside bracket expressions
to a CRLF-first alternation over the vertical characters. A focused root
regression checks substring, replacement, and anchored matching; the wire
contract covers the same behavior.

ICU `\Q...\E` quotes regex operators literally. MySQL probes confirmed
literal plus signs, brackets, and backslashes, quotes that continue to the
end of the pattern, literal `\E` outside a quote, and quoted characters inside
a bracket expression. The compiler escapes the quoted span before passing it
to .NET, preserving the enclosing bracket state. The root regression and wire
contract cover these cases.

MySQL's `\w`/`\W` use the same Unicode word set as `[[:word:]]`, including
ZWNJ and ZWJ. .NET's `\w` omits those join controls. The shared character-class
translation now uses one definition for the POSIX and shorthand forms, both
standalone and within bracket expressions. Word-boundary assertions remain
subject to the underlying .NET engine's rules.

Native probes found `\b` and `\B` at audited join-control and combining-mark
boundaries match .NET's assertions. Within brackets, MySQL instead reads
`[\b]` and `[\B]` as the literal letters `b` and `B`; .NET reads `\b` as
backspace. The compiler translates these bracket forms, including when they
appear beside a negated shorthand such as `[\W\b]`.

Pattern diagnostics now run after literal-quote normalization. MySQL accepts
`\Q{3,2}\E`, `\Q{3,2}`, and `\Q{\E` as literals, while rejecting an
unquoted reversed interval with error 3693. The previous validation order
rejected the quoted interval before the quote could protect it.

The shared REGEXP compiler translates these classes to .NET character
classes. A root Expecto regression checks the oracle matrix through
`REGEXP_LIKE`; the operator and other REGEXP functions use the same compiler.
`just check` passes 3,221 tests. The `regexp-posix-classes` native wire contract
matches all fourteen queries, including text versus numeric `ZEROFILL`
operands and row-varying pattern/match-type evaluation, at
`torture/artifacts/runs/20261009T171004941-99821/contracts`.
The full Docker lane retains the same nine pre-existing identifier-case
differences because that oracle uses `lower_case_table_names=0`.

All fourteen standard POSIX class names now have translations. The translations
are bounded approximations of ICU properties: for example, MySQL counts the
Roman numeral `Ⅳ` as `upper` and `ⅳ` as `lower`, while .NET's `Lu`/`Ll`
categories do not. Supplementary Unicode scalars are another open boundary:
MySQL `REGEXP_SUBSTR('😀','[x[:^alpha:]]')` returns the complete emoji, while
the underlying .NET regex matches only its high UTF-16 surrogate. The same
problem exists for a standalone negated POSIX class. Other ICU-only grammar
and error-code distinctions remain open. MySQL accepts script properties such
as `\p{Greek}` and `\p{Script=Greek}`. .NET's `IsGreek` is a narrower Unicode
block, so direct property-name substitution would be incorrect.
