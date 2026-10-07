/// Full-text scoring for `MATCH (cols) AGAINST (...)` — natural language,
/// boolean, and query-expansion modes over an immutable inverted index.
/// Documents combine row-wide term counts but retain column boundaries for
/// exact phrases.
///
/// Relevance follows InnoDB's documented formula, oracle-verified against
/// MySQL 8.4.11: `rank = Σ_term TF × IDF²` with `IDF = log10(N / df)`.
/// A term present in every document scores a tiny positive epsilon per
/// occurrence instead of 0 (observed 1.885928e-9 = (4.3427e-5)² per TF on a
/// live server), which is why `WHERE MATCH(...) AGAINST ('everywhere-word')`
/// matches every row on real MySQL — modeled as an IDF floor.
module Fsdb.FullText

open System
open Fsdb.Collation

/// Minimum indexed word length; ngram indexes use their configured token size.
let minTokenLength = 3

/// `@@innodb_ft_max_token_size`'s default.
let maxTokenLength = 84

/// `@@ft_query_expansion_limit`'s default: how many top-ranked documents
/// seed the second pass of WITH QUERY EXPANSION.
let private queryExpansionLimit = 20

let ngramTokenSize = StorageOptions.defaults.NgramTokenSize

type Tokenizer =
    | Words
    | Ngrams of size: int

[<RequireQualifiedAccess>]
type internal StopwordPolicy =
    | BuiltIn
    | Disabled

/// Rules captured by a document when its postings are built.
type internal IndexingRules =
    { Tokenizer: Tokenizer
      Stopwords: StopwordPolicy }

let tryTokenizer = function
    | None -> Some Words
    | Some name when String.Equals(name, "ngram", StringComparison.OrdinalIgnoreCase) -> Some(Ngrams ngramTokenSize)
    | Some _ -> None

/// InnoDB's default stopword list, verbatim from a live 8.4.11's
/// `INFORMATION_SCHEMA.INNODB_FT_DEFAULT_STOPWORD` (where "the" really
/// does appear twice there).
let defaultStopwords =
    [ "a"; "about"; "an"; "are"; "as"; "at"; "be"; "by"; "com"; "de"
      "en"; "for"; "from"; "how"; "i"; "in"; "is"; "it"; "la"; "of"
      "on"; "or"; "that"; "the"; "this"; "to"; "was"; "what"; "when"
      "where"; "who"; "will"; "with"; "und"; "the"; "www" ]

let private stopwords = Set.ofList defaultStopwords

/// The observed IDF floor for a term every document contains — see the
/// module doc. sqrt of the per-occurrence epsilon rank a live 8.4.11
/// reports (1.885928302414186e-9).
let private idfFloor = 4.3427276e-5

let private isWordChar (c: char) =
    Char.IsLetterOrDigit c
    || c = '_'
    || (match Globalization.CharUnicodeInfo.GetUnicodeCategory c with
        | Globalization.UnicodeCategory.NonSpacingMark
        | Globalization.UnicodeCategory.SpacingCombiningMark
        | Globalization.UnicodeCategory.EnclosingMark -> true
        | _ -> false)

/// MySQL's word characters are alphanumerics plus `_` and an *in-word*
/// apostrophe (`O'Brien` is one token, a trailing `'` is punctuation).
let private rawTokens (text: string) : string[] =
    let tokens = ResizeArray<string>()
    let current = Text.StringBuilder()

    let flush () =
        // Strip apostrophes that ended up at the token edge.
        let t = current.ToString().Trim('\'')
        if t.Length > 0 then tokens.Add t
        current.Clear() |> ignore

    for c in text do
        if isWordChar c then current.Append c |> ignore
        elif c = '\'' && current.Length > 0 then current.Append c |> ignore
        else flush ()

    flush ()
    tokens.ToArray()

let private ngrams size (text: string) =
    let boundary (rune: Text.Rune) =
        rune.IsAscii && not (Text.Rune.IsLetterOrDigit rune || rune.Value = int '_')

    text.EnumerateRunes()
    |> Seq.toArray
    |> Array.windowed size
    |> Array.choose (fun runes ->
        if Array.exists boundary runes then None
        else runes |> Array.map string |> String.concat "" |> Some)

let private rawTokensWith tokenizer text =
    match tokenizer with
    | Words -> rawTokens text
    | Ngrams size -> ngrams size text

let tokenize (text: string) : string[] = rawTokens text |> Array.map _.ToLowerInvariant()

// ---------------------------------------------------------------------------
// Corpus compatibility wrapper over the immutable index.
// ---------------------------------------------------------------------------

type private Token =
    { Text: string
      Key: string }

type private Document =
    { Fields: Token[][]
      Rules: IndexingRules }

type Index<'id when 'id: comparison> =
    private
        { Documents: Map<'id, Document>
          Postings: Map<string, Map<'id, int>>
          PrefixPostings: Map<string, Map<'id, int>>
          Collation: Collation
          Rules: IndexingRules }

/// Query state is separate from the index maintained for future commits.
type internal ReadView<'id when 'id: comparison> =
    private
        { Documents: Map<'id, Document>
          Postings: Map<string, Map<'id, int>>
          PrefixPostings: Map<string, Map<'id, int>>
          Collation: Collation
          Rules: IndexingRules
          DocumentCount: int
          VisibleDocumentIds: Set<'id> option }

let internal readView (index: Index<'id>) : ReadView<'id> =
    { Documents = index.Documents
      Postings = index.Postings
      PrefixPostings = index.PrefixPostings
      Collation = index.Collation
      Rules = index.Rules
      DocumentCount = index.Documents.Count
      VisibleDocumentIds = None }

let private intersectCandidates left right =
    match left, right with
    | Some left, Some right -> Some(Set.intersect left right)
    | Some ids, None
    | None, Some ids -> Some ids
    | None, None -> None

let internal restrictReadView visibleDocumentIds (view: ReadView<'id>) =
    { view with VisibleDocumentIds = intersectCandidates view.VisibleDocumentIds (Some visibleDocumentIds) }

let internal withReadDocumentCount count (view: ReadView<'id>) =
    if count < 0 then invalidArg "count" "Full-text document count cannot be negative"
    { view with DocumentCount = count }

let private candidatesInView candidateIds (view: ReadView<'id>) =
    intersectCandidates candidateIds view.VisibleDocumentIds

type Corpus =
    private
        { Order: int[]
          Index: Index<int> }

let private tokenWith (collation: Collation) text =
    { Text = text
      Key = collation.KeyOf text }

let private tokensWith tokenizer collation text =
    rawTokensWith tokenizer text |> Array.map (tokenWith collation)

let private runeLength (text: string) = text.EnumerateRunes() |> Seq.length

let private containsNgramStopword policy (text: string) =
    let lower = text.ToLowerInvariant()
    policy = StopwordPolicy.BuiltIn
    && (stopwords |> Set.exists (fun word -> lower.Contains(word, StringComparison.Ordinal)))

let private isSearchableWord policy (token: Token) =
    token.Text.Length >= minTokenLength
    && token.Text.Length <= maxTokenLength
    && (policy = StopwordPolicy.Disabled || not (Set.contains (token.Text.ToLowerInvariant()) stopwords))

/// A token that survives the configured length and stopword rules.
let private isSearchable rules (token: Token) =
    match rules.Tokenizer with
    | Words -> isSearchableWord rules.Stopwords token
    | Ngrams size ->
        runeLength token.Text = size && not (containsNgramStopword rules.Stopwords token.Text)

/// Boolean terms can address postings created with an earlier ngram size.
let private isBooleanSearchable rules token =
    match rules.Tokenizer with
    | Words -> isSearchable rules token
    | Ngrams _ -> not (containsNgramStopword rules.Stopwords token.Text)

let private prefixKeys (collation: Collation) (token: Token) =
    let mutable length = 0
    [| for rune in token.Text.EnumerateRunes() do
           length <- length + rune.Utf16SequenceLength
           collation.KeyOf(token.Text.Substring(0, length)) |]
    |> Array.distinct

let private removePosting id key postings =
    match Map.tryFind key postings with
    | None -> postings
    | Some rows ->
        let remaining = Map.remove id rows
        if remaining.IsEmpty then Map.remove key postings else Map.add key remaining postings

let private emptyIndexWith policy tokenizer (collation: Collation) : Index<'id> =
    { Documents = Map.empty
      Postings = Map.empty
      PrefixPostings = Map.empty
      Collation = collation
      Rules = { Tokenizer = tokenizer; Stopwords = policy } }

let emptyIndex collation = emptyIndexWith StopwordPolicy.BuiltIn Words collation

/// Selects rules for queries and future writes, retaining each document's history.
let internal withIndexingRules rules (index: Index<'id>) =
    { index with Rules = rules }

let internal activeRules (index: Index<'id>) = index.Rules

/// Selects the tokenizer for queries and future writes, retaining existing postings.
let internal withTokenizer tokenizer (index: Index<'id>) =
    withIndexingRules { index.Rules with Tokenizer = tokenizer } index

let internal activeTokenizer (index: Index<'id>) = index.Rules.Tokenizer

let internal withNgramTokenSize size (index: Index<'id>) =
    match index.Rules.Tokenizer with
    | Words -> index
    | Ngrams _ -> withTokenizer (Ngrams size) index

let removeDocument (id: 'id) (index: Index<'id>) : Index<'id> =
    match Map.tryFind id index.Documents with
    | None -> index
    | Some document ->
        let tokens = Array.concat document.Fields
        let frequencies = tokens |> Array.countBy _.Key
        let prefixes =
            tokens
            |> Array.filter (isSearchable document.Rules)
            |> Array.collect (prefixKeys index.Collation)
            |> Array.countBy (fun key -> key)

        let postings =
            frequencies
            |> Array.fold (fun postings (key, _) -> removePosting id key postings) index.Postings

        let prefixPostings =
            prefixes
            |> Array.fold (fun postings (key, _) -> removePosting id key postings) index.PrefixPostings

        { index with
            Documents = Map.remove id index.Documents
            Postings = postings
            PrefixPostings = prefixPostings }

let private addDocumentData id (document: Document) (index: Index<'id>) =
    let index = removeDocument id index
    let tokenizer = document.Rules.Tokenizer
    let tokens = Array.concat document.Fields
    let postingTokens =
        match tokenizer with
        | Words -> tokens
        | Ngrams _ -> tokens |> Array.filter (isSearchable document.Rules)

    let postings =
        postingTokens
        |> Array.groupBy _.Key
        |> Array.fold
            (fun postings (key, tokens) ->
                let rows = postings |> Map.tryFind key |> Option.defaultValue Map.empty
                Map.add key (Map.add id tokens.Length rows) postings)
            index.Postings

    let prefixPostings =
        tokens
        |> Array.filter (isSearchable document.Rules)
        |> Array.collect (prefixKeys index.Collation)
        |> Array.countBy (fun key -> key)
        |> Array.fold
            (fun postings (key, frequency) ->
                let rows = postings |> Map.tryFind key |> Option.defaultValue Map.empty
                Map.add key (Map.add id frequency rows) postings)
            index.PrefixPostings

    { index with
        Documents = Map.add id document index.Documents
        Postings = postings
        PrefixPostings = prefixPostings }

let internal addDocumentFieldsWithRules rules id texts (index: Index<'id>) =
    // Phrase positions retain tokens omitted from ngram postings.
    let fields = texts |> List.map (tokensWith rules.Tokenizer index.Collation) |> List.toArray
    addDocumentData id { Fields = fields; Rules = rules } index

let internal sameDocument id (left: Index<'id>) (right: Index<'id>) =
    match Map.tryFind id left.Documents, Map.tryFind id right.Documents with
    | Some left, Some right -> obj.ReferenceEquals(left, right)
    | _ -> false

let internal mergeDocuments sourceRowIds (baseline: Index<'id>) (source: Index<'id>) (target: Index<'id>) =
    if obj.ReferenceEquals(baseline.Documents, source.Documents) then target
    else
        let targetId sourceId = Map.tryFind sourceId sourceRowIds |> Option.defaultValue sourceId
        let ids = Set.union (baseline.Documents |> Map.keys |> Set.ofSeq) (source.Documents |> Map.keys |> Set.ofSeq)
        (target, ids)
        ||> Set.fold (fun target id ->
            if sameDocument id baseline source then target
            else
                match Map.tryFind id source.Documents with
                | Some document -> addDocumentData (targetId id) document target
                | None -> removeDocument (targetId id) target)

let addDocumentFields id texts (index: Index<'id>) =
    addDocumentFieldsWithRules index.Rules id texts index

let addDocument id text index = addDocumentFields id [ text ] index

let internal buildIndexWithStopwords policy tokenizer collation documents =
    documents
    |> Seq.fold (fun index (id, fields) -> addDocumentFields id fields index) (emptyIndexWith policy tokenizer collation)

let internal documentRules id (index: Index<'id>) =
    index.Documents |> Map.tryFind id |> Option.map _.Rules

let internal rulesInUse (index: Index<'id>) =
    seq {
        yield index.Rules
        for KeyValue(_, document) in index.Documents do
            yield document.Rules
    }
    |> Seq.distinct
    |> Seq.toArray

let internal documentTokenizer id index =
    documentRules id index |> Option.map _.Tokenizer

let internal activeStopwords (index: Index<'id>) = index.Rules.Stopwords

let internal buildIndexWithDocumentSettings rules collation documents =
    documents
    |> Seq.fold (fun index (id, documentRules, fields) ->
        addDocumentFieldsWithRules documentRules id fields index) (emptyIndexWith rules.Stopwords rules.Tokenizer collation)

let internal buildIndexWithDocumentTokenizers tokenizer collation documents =
    let rules = { Tokenizer = tokenizer; Stopwords = StopwordPolicy.BuiltIn }
    documents
    |> Seq.map (fun (id, documentTokenizer, fields) -> id, { rules with Tokenizer = documentTokenizer }, fields)
    |> buildIndexWithDocumentSettings rules collation

let buildIndexWithFields tokenizer (collation: Collation) (documents: ('id * string list) seq) : Index<'id> =
    documents
    |> Seq.map (fun (id, fields) -> id, tokenizer, fields)
    |> buildIndexWithDocumentTokenizers tokenizer collation

let buildIndexWithTokenizer tokenizer collation documents =
    documents |> Seq.map (fun (id, text) -> id, [ text ]) |> buildIndexWithFields tokenizer collation

let buildIndexWith collation documents = buildIndexWithTokenizer Words collation documents

let buildCorpusWith (collation: Collation) (docs: string seq) : Corpus =
    let documents = docs |> Seq.indexed |> Array.ofSeq
    { Order = documents |> Array.map fst
      Index = buildIndexWith collation documents }

let buildCorpus (docs: string seq) : Corpus =
    buildCorpusWith defaultCollation docs

let private idf (view: ReadView<'id>) (df: int) : float =
    if df = 0 then 0.0
    else max (log10 (float view.DocumentCount / float df)) idfFloor

let private termScoresWithin (candidateIds: Set<'id> option) (view: ReadView<'id>) (term: string) : Map<'id, float> =
    match Map.tryFind term view.Postings with
    | None -> Map.empty
    | Some rows ->
        let weight = idf view rows.Count
        let scale = weight * weight

        match candidateIds with
        | None -> rows |> Map.map (fun _ frequency -> float frequency * scale)
        | Some candidates ->
            candidates
            |> Seq.choose (fun id -> Map.tryFind id rows |> Option.map (fun frequency -> id, float frequency * scale))
            |> Map.ofSeq

let private termScores view term = termScoresWithin None view term

let private exactPhraseMatches (doc: Token[]) (words: Token[]) =
    if words.Length = 0 then
        false
    else
        seq { 0 .. doc.Length - words.Length }
        |> Seq.exists (fun start ->
            words
            |> Array.indexed
            |> Array.forall (fun (offset, word) -> doc.[start + offset].Key = word.Key))

let private phraseCandidates (view: ReadView<'id>) (words: Token[]) =
    words
    // InnoDB omits short terms and stopwords from postings but retains their
    // positions after the first searchable word in a phrase.
    |> Array.filter (isBooleanSearchable view.Rules)
    |> Array.distinctBy _.Key
    |> Array.map (fun word ->
        view.Postings
        |> Map.tryFind word.Key
        |> Option.map (Map.keys >> Set.ofSeq)
        |> Option.defaultValue Set.empty)
    |> Array.sortBy _.Count
    |> function
        | [||] -> Set.empty
        | sets -> sets |> Array.reduce Set.intersect

// ---------------------------------------------------------------------------
// Natural language mode.
// ---------------------------------------------------------------------------

/// Distinct searchable terms of a natural-language query.
let private queryTokens (view: ReadView<'id>) query =
    tokensWith view.Rules.Tokenizer view.Collation query

let private naturalTerms (view: ReadView<'id>) (query: string) : string[] =
    let tokens = queryTokens view query
    let terms =
        match view.Rules.Tokenizer with
        | Words -> tokens |> Array.filter (isSearchable view.Rules)
        // A stopped spelling can still match an indexed collation equivalent.
        | Ngrams _ -> tokens

    terms
    |> Array.map _.Key
    |> Array.distinct

/// Sum of independent terms' per-document contributions.
/// Accumulates into one result map rather than retaining one map per term,
/// so peak memory stays O(rows), not
/// O(terms × rows) for a query with many distinct terms.
let private sumTermScoresWithin (candidateIds: Set<'id> option) (view: ReadView<'id>) (terms: string[]) : Map<'id, float> =
    match terms with
    | [||] -> Map.empty
    | [| term |] -> termScoresWithin candidateIds view term
    | _ ->
        terms
        |> Array.skip 1
        |> Array.fold
            (fun scores term ->
                termScoresWithin candidateIds view term
                |> Map.fold
                    (fun scores id score ->
                        Map.change id (fun current -> Some(score + Option.defaultValue 0.0 current)) scores)
                    scores)
            (termScoresWithin candidateIds view terms.[0])

type private NaturalClause =
    | NaturalWord of key: string
    | NaturalPhrase of Token[]

let private naturalWordClauses (view: ReadView<'id>) (query: string) =
    let words text = tokensWith Words view.Collation text
    let plain text = words text |> Array.filter (isSearchableWord view.Rules.Stopwords) |> Array.map (fun word -> NaturalWord word.Key)
    let clauses = ResizeArray<NaturalClause>()
    let mutable start = 0
    while start < query.Length do
        let opening = query.IndexOf('"', start)
        let closing = if opening < 0 then -1 else query.IndexOf('"', opening + 1)
        if closing < 0 then
            clauses.AddRange(plain (query.Substring start))
            start <- query.Length
        else
            clauses.AddRange(plain (query.Substring(start, opening - start)))
            words (query.Substring(opening + 1, closing - opening - 1))
            |> Array.skipWhile (isSearchableWord view.Rules.Stopwords >> not)
            |> NaturalPhrase
            |> clauses.Add
            start <- closing + 1
    clauses.ToArray()

let private naturalClauseKeys policy = function
    | NaturalWord key -> [| key |]
    | NaturalPhrase words -> words |> Array.filter (isSearchableWord policy) |> Array.map _.Key

let private needsNaturalWordEvaluation (view: ReadView<'id>) (query: string) =
    match view.Rules.Tokenizer with
    | Ngrams _ -> false
    | Words ->
        if query.Contains '"' then true
        else
            let terms = queryTokens view query |> Array.filter (isSearchableWord view.Rules.Stopwords)
            terms.Length <> (terms |> Array.distinctBy _.Key).Length

type private NaturalMatches<'id when 'id: comparison> =
    { Occurrences: Map<string, int>
      Documents: Map<string, Set<'id>> }

let private naturalClauseDocuments (view: ReadView<'id>) = function
    | NaturalWord key ->
        view.Postings
        |> Map.tryFind key
        |> Option.map (Map.keys >> Set.ofSeq)
        |> Option.defaultValue Set.empty
    | NaturalPhrase words ->
        phraseCandidates view words
        |> Set.filter (fun id ->
            view.Documents.[id].Fields |> Array.exists (fun field -> exactPhraseMatches field words))

/// Matching terms retain their row sets separately from query occurrence counts.
/// Repeated query words increase MySQL's document frequency, not a row's TF.
let private naturalWordMatches candidateIds (view: ReadView<'id>) clauses =
    let addClause matches clause =
        let ids = naturalClauseDocuments view clause
        let ids = candidateIds |> Option.map (Set.intersect ids) |> Option.defaultValue ids
        if ids.IsEmpty then matches
        else
            naturalClauseKeys view.Rules.Stopwords clause
            |> Array.fold (fun matches key ->
                Map.change key (fun previous ->
                    Some(Set.union ids (Option.defaultValue Set.empty previous))) matches) matches

    { Occurrences = clauses |> Array.collect (naturalClauseKeys view.Rules.Stopwords) |> Array.countBy id |> Map.ofArray
      Documents = clauses |> Array.fold addClause Map.empty }

let private scoreNaturalWordMatches (view: ReadView<'id>) matches =
    matches.Documents
    |> Map.fold (fun scores key ids ->
        let rows = view.Postings.[key]
        let frequency = float rows.Count * float matches.Occurrences.[key]
        let weight = max (abs (log10 (float view.DocumentCount / frequency))) idfFloor
        ids |> Set.fold (fun scores id ->
            let contribution = float rows.[id] * weight * weight
            Map.change id (fun current -> Some(contribution + Option.defaultValue 0.0 current)) scores) scores) Map.empty

let internal naturalScoresInView candidateIds (view: ReadView<'id>) query =
    let candidateIds = candidatesInView candidateIds view
    if needsNaturalWordEvaluation view query then
        naturalWordClauses view query
        |> naturalWordMatches candidateIds view
        |> scoreNaturalWordMatches view
    else
        sumTermScoresWithin candidateIds view (naturalTerms view query)

let naturalScores (index: Index<'id>) (query: string) : Map<'id, float> =
    naturalScoresInView None (readView index) query

let internal naturalScoresWithin (candidateIds: Set<'id>) (index: Index<'id>) (query: string) : Map<'id, float> =
    naturalScoresInView (Some candidateIds) (readView index) query

let internal tryNaturalSingleTermScoresDictionaryInView
    (candidateIds: Set<'id> option)
    (view: ReadView<'id>)
    (query: string)
    : Collections.Generic.Dictionary<'id, float> option =
    let candidateIds = candidatesInView candidateIds view
    match naturalTerms view query with
    | [| term |] when not (needsNaturalWordEvaluation view query) ->
        match Map.tryFind term view.Postings with
        | None -> Collections.Generic.Dictionary() |> Some
        | Some rows ->
            let weight = idf view rows.Count
            let scale = weight * weight
            let capacity = candidateIds |> Option.map _.Count |> Option.defaultValue rows.Count |> min rows.Count
            let scores = Collections.Generic.Dictionary<'id, float>(capacity)

            match candidateIds with
            | None ->
                for KeyValue(id, frequency) in rows do
                    scores.Add(id, float frequency * scale)
            | Some candidates ->
                for id in candidates do
                    match Map.tryFind id rows with
                    | Some frequency -> scores.Add(id, float frequency * scale)
                    | None -> ()

            Some scores
    | _ -> None

let internal tryNaturalSingleTermScoresDictionaryWithin candidateIds index query =
    tryNaturalSingleTermScoresDictionaryInView candidateIds (readView index) query

let internal tryNaturalSingleTermScoresDictionary index query =
    tryNaturalSingleTermScoresDictionaryWithin None index query

let private scoresInCorpusOrder (corpus: Corpus) (scores: Map<int, float>) =
    corpus.Order |> Array.map (fun id -> scores |> Map.tryFind id |> Option.defaultValue 0.0)

let naturalScoresOf (corpus: Corpus) (query: string) : float[] =
    naturalScores corpus.Index query |> scoresInCorpusOrder corpus

// ---------------------------------------------------------------------------
// Boolean mode. Query grammar (recursive descent):
//   node    := [op] term
//   op      := '+' | '-' | '>' | '<' | '~'
//   term    := word ['*'] | '"' words '"' ['@' N] | '(' node* ')'
// Contributions are TF×IDF² like natural mode; oracle-verified InnoDB
// behavior for the modifiers: `>` adds +1.0 to a matched term's
// contribution and `<` subtracts 1.0 (a weak match can go negative);
// `~` zeroes a matched term's contribution (observed on 8.4.11 — the
// manual's "lowers" is MyISAM's older behavior). `@N` proximity requires
// every distinct quoted word to span fewer than N positions; word order and
// repeated query words do not add requirements.
// ---------------------------------------------------------------------------

type private BoolOp =
    | Must
    | MustNot
    | Optional
    | Raise
    | Lower
    | Soft

type private BoolTerm =
    | BWord of term: Token * prefix: bool
    | BPhrase of words: Token[] * proximity: int option
    | BGroup of (BoolOp * BoolTerm) list

type private FlatPosting<'id when 'id: comparison> =
    { Operator: BoolOp
      Rows: Map<'id, int>
      Scale: float }

[<Struct>]
type private BooleanScoreState =
    { Matched: bool
      Excluded: bool
      Score: float }

let private emptyBooleanScore =
    { Matched = false
      Excluded = false
      Score = 0.0 }

let private addBooleanContribution operator contribution state =
    match operator, contribution with
    | Must, Some score ->
        { state with
            Matched = true
            Score = state.Score + score }
    | Must, None -> { state with Excluded = true }
    | MustNot, Some _ -> { state with Excluded = true }
    | MustNot, None -> state
    | Optional, Some score ->
        { state with
            Matched = true
            Score = state.Score + score }
    | Raise, Some score ->
        { state with
            Matched = true
            Score = state.Score + score + 1.0 }
    | Lower, Some score ->
        { state with
            Matched = true
            Score = state.Score + score - 1.0 }
    | Soft, Some _ -> { state with Matched = true }
    | (Optional | Raise | Lower | Soft), None -> state

let private tryBooleanScore state =
    if state.Matched && not state.Excluded then Some state.Score else None

let private parseBooleanQuery rules (collation: Collation) (query: string) : (BoolOp * BoolTerm) list =
    let policy, tokenizer = rules.Stopwords, rules.Tokenizer
    let mutable i = 0
    let len = query.Length

    let rec skipSpace () =
        if i < len && Char.IsWhiteSpace query.[i] then
            i <- i + 1
            skipSpace ()

    let readWord () =
        let start = i
        while i < len && (isWordChar query.[i] || (query.[i] = '\'' && i > start)) do
            i <- i + 1
        let text = query.Substring(start, i - start).Trim('\'')
        tokenWith collation text

    let phraseWords phrase =
        let words =
            match tokenizer with
            | Words -> tokensWith tokenizer collation phrase
            | Ngrams size ->
                rawTokens phrase
                |> Array.collect (fun word ->
                    if runeLength word < size then [| tokenWith collation word |]
                    else tokensWith tokenizer collation word)

        words
        |> Array.skipWhile (fun word ->
            match tokenizer with
            | Words -> not (isSearchable rules word)
            | Ngrams _ -> containsNgramStopword policy word.Text)

    // Cap parenthesis nesting so a query like "((((...))))" with thousands
    // of groups can't overflow the recursive-descent stack (a
    // StackOverflowException is not catchable and would kill the process).
    // Past the cap, an open paren is treated as ignorable punctuation.
    let maxDepth = 64

    let rec nodes (depth: int) (stopAtParen: bool) : (BoolOp * BoolTerm) list =
        let acc = ResizeArray()
        let mutable go = true

        while go do
            skipSpace ()

            if i >= len then go <- false
            elif query.[i] = ')' then
                if stopAtParen then i <- i + 1
                else i <- i + 1 // stray close: skip, like MySQL's lenient parser
                go <- false
            else
                let op =
                    match query.[i] with
                    | '+' -> i <- i + 1; Must
                    | '-' -> i <- i + 1; MustNot
                    | '>' -> i <- i + 1; Raise
                    | '<' -> i <- i + 1; Lower
                    | '~' -> i <- i + 1; Soft
                    | _ -> Optional

                skipSpace ()

                if i >= len then go <- false
                elif query.[i] = '(' then
                    i <- i + 1
                    if depth < maxDepth then acc.Add(op, BGroup(nodes (depth + 1) true))
                elif query.[i] = '"' then
                    i <- i + 1
                    let start = i
                    while i < len && query.[i] <> '"' do
                        i <- i + 1
                    let phrase = query.Substring(start, i - start)
                    if i < len then i <- i + 1 // closing quote
                    skipSpace ()

                    let proximity =
                        if i < len && query.[i] = '@' then
                            i <- i + 1
                            let ds = i
                            while i < len && Char.IsDigit query.[i] do
                                i <- i + 1
                            match Int32.TryParse(query.Substring(ds, i - ds)) with
                            | true, n -> Some n
                            | _ -> None
                        else
                            None

                    let words = phraseWords phrase

                    // MySQL's ngram phrases ignore the proximity suffix.
                    let proximity =
                        match tokenizer with
                        | Words -> proximity
                        | Ngrams _ -> None

                    acc.Add(op, BPhrase(words, proximity))
                elif isWordChar query.[i] then
                    let w = readWord ()
                    let prefix = i < len && query.[i] = '*'
                    if prefix then i <- i + 1
                    if w.Text.Length > 0 then
                        let term =
                            match tokenizer with
                            | Words -> BWord(w, prefix)
                            | Ngrams size when runeLength w.Text < size -> BWord(w, prefix)
                            | Ngrams _ ->
                                let words = tokensWith tokenizer collation w.Text |> Array.skipWhile (isSearchable rules >> not)
                                match words with
                                | [| word |] -> BWord(word, false)
                                | _ -> BPhrase(words, None)
                        acc.Add(op, term)
                else
                    // Punctuation MySQL's parser ignores.
                    i <- i + 1

        List.ofSeq acc

    nodes 0 false

let private proximityMatches (doc: Token[]) (words: Token[]) distance =
    let required = words |> Array.map _.Key |> Set.ofArray

    if required.IsEmpty then
        false
    elif required.Count = 1 then
        doc |> Array.exists (fun token -> required.Contains token.Key)
    else
        let frequencies = Collections.Generic.Dictionary<string, int>()
        let mutable covered = 0
        let mutable left = 0
        let mutable matched = false

        for right in 0 .. doc.Length - 1 do
            let rightKey = doc.[right].Key

            if required.Contains rightKey then
                match frequencies.TryGetValue rightKey with
                | true, count -> frequencies.[rightKey] <- count + 1
                | false, _ ->
                    frequencies.Add(rightKey, 1)
                    covered <- covered + 1

            while not matched && covered = required.Count do
                if right - left < distance then
                    matched <- true
                else
                    let leftKey = doc.[left].Key

                    if required.Contains leftKey then
                        let count = frequencies.[leftKey] - 1

                        if count = 0 then
                            frequencies.Remove leftKey |> ignore
                            covered <- covered - 1
                        else
                            frequencies.[leftKey] <- count

                    left <- left + 1

        matched

/// `TF×IDF²` for a prefix wildcard, whose document frequency belongs to the
/// prefix posting rather than any one complete-token posting.
let private scoresFromTfsWithDocumentFrequency
    (view: ReadView<'id>)
    (documentFrequency: int)
    (tfs: Map<'id, int>)
    : Map<'id, float> =
    let weight = idf view documentFrequency
    tfs |> Map.map (fun _ tf -> float tf * weight * weight)

let private restrictScores (candidateIds: Set<'id> option) (scores: Map<'id, 'value>) =
    match candidateIds with
    | None -> scores
    | Some candidates ->
        candidates
        |> Seq.choose (fun id -> Map.tryFind id scores |> Option.map (fun score -> id, score))
        |> Map.ofSeq

let private booleanCandidates (results: (BoolOp * Map<'id, float>) list) : seq<'id> =
    let required =
        results
        |> List.choose (function
            | Must, scores -> Some scores
            | _ -> None)

    match required with
    | [] ->
        results
        |> List.fold
            (fun candidates (operator, scores) ->
                if operator = MustNot then
                    candidates
                else
                    scores |> Map.fold (fun candidates id _ -> Set.add id candidates) candidates)
            Set.empty
        |> Set.toSeq
    | required ->
        let smallest = required |> List.minBy _.Count

        smallest
        |> Map.keys
        |> Seq.filter (fun id -> required |> List.forall (Map.containsKey id))

/// Per-document contribution for one boolean term.
let rec private evalTerm
    (candidateIds: Set<'id> option)
    (view: ReadView<'id>)
    (term: BoolTerm)
    : Map<'id, float> =
    match term with
    | BWord(term, false) when not (isBooleanSearchable view.Rules term) ->
        // Rejected words can still occupy positions inside a longer phrase.
        Map.empty
    | BWord(term, false) ->
        termScoresWithin candidateIds view term.Key
    | BWord(term, true) ->
        // Prefix wildcards bypass stopword and minimum-length rules.
        let rows =
            view.PrefixPostings
            |> Map.tryFind term.Key
            |> Option.defaultValue Map.empty

        rows
        |> restrictScores candidateIds
        |> scoresFromTfsWithDocumentFrequency view rows.Count
    | BPhrase(words, proximity) ->
        let candidates =
            phraseCandidates view words
            |> fun candidates -> candidateIds |> Option.map (Set.intersect candidates) |> Option.defaultValue candidates

        let terms =
            words
            |> Array.filter (isBooleanSearchable view.Rules)
            |> Array.distinctBy _.Key
            |> Array.choose (fun word ->
                view.Postings
                |> Map.tryFind word.Key
                |> Option.map (fun rows ->
                    let weight = idf view rows.Count
                    rows, weight * weight))

        candidates
        |> Seq.choose (fun id ->
            let fields = view.Documents.[id].Fields
            let matches =
                match proximity with
                | None -> fields |> Array.exists (fun tokens -> exactPhraseMatches tokens words)
                | Some distance -> proximityMatches (Array.concat fields) words distance

            if matches then
                terms
                |> Array.sumBy (fun (rows, scale) -> float rows.[id] * scale)
                |> fun score -> Some(id, score)
            else
                None)
        |> Map.ofSeq
    | BGroup nodes ->
        evalNodes candidateIds view nodes

/// Per-document score over a node list — the boolean combination:
/// a document is absent when a `+` term misses or a `-` term
/// hits; otherwise matched when anything matched, scoring the sum of the
/// modifier-adjusted contributions.
and private evalNodes
    (candidateIds: Set<'id> option)
    (view: ReadView<'id>)
    (nodes: (BoolOp * BoolTerm) list)
    : Map<'id, float> =
    let results = nodes |> List.map (fun (op, term) -> op, evalTerm candidateIds view term)

    booleanCandidates results
    |> Seq.choose (fun id ->
        results
        |> List.fold
            (fun state (operator, scores) ->
                addBooleanContribution operator (Map.tryFind id scores) state)
            emptyBooleanScore
        |> tryBooleanScore
        |> Option.map (fun score -> id, score))
    |> Map.ofSeq

let private visibleBooleanScore score =
    if score = 0.0 then idfFloor * idfFloor else score

let internal booleanScoresInView candidateIds (view: ReadView<'id>) (query: string) =
    let candidateIds = candidatesInView candidateIds view
    evalNodes candidateIds view (parseBooleanQuery view.Rules view.Collation query)
    |> Map.map (fun _ score -> visibleBooleanScore score)

let booleanScores (index: Index<'id>) (query: string) : Map<'id, float> =
    booleanScoresInView None (readView index) query

let internal booleanScoresWithin (candidateIds: Set<'id>) (index: Index<'id>) (query: string) =
    booleanScoresInView (Some candidateIds) (readView index) query

let internal tryFlatBooleanScoresDictionaryInView
    (candidateIds: Set<'id> option)
    (view: ReadView<'id>)
    (query: string)
    : Collections.Generic.Dictionary<'id, float> option =
    let candidateIds = candidatesInView candidateIds view
    let rec flatTerms (found: (BoolOp * Token * bool) list) =
        function
        | [] when not found.IsEmpty -> Some(List.rev found)
        | (op, BWord(term, prefix)) :: rest -> flatTerms ((op, term, prefix) :: found) rest
        | _ -> None

    parseBooleanQuery view.Rules view.Collation query
    |> flatTerms []
    |> Option.map (fun terms ->
        let postingFor (term: Token, prefix) =
            if prefix then Map.tryFind term.Key view.PrefixPostings
            elif isBooleanSearchable view.Rules term then Map.tryFind term.Key view.Postings
            else None

        let postings =
            terms
            |> List.map (fun (op, term, prefix) ->
                let rows = postingFor (term, prefix) |> Option.defaultValue Map.empty
                let weight = idf view rows.Count
                { Operator = op
                  Rows = rows
                  Scale = weight * weight })

        let positiveTerms =
            postings
            |> List.filter (fun posting -> posting.Operator <> MustNot)

        let requiredTerms =
            postings
            |> List.filter (fun posting -> posting.Operator = Must)

        let smallestRequiredPosting =
            match requiredTerms with
            | [] -> None
            | terms ->
                terms
                |> List.minBy (fun posting -> posting.Rows.Count)
                |> fun posting -> Some posting.Rows

        let scoreCandidates (candidates: seq<'id>) (capacity: int) =
            let scores = Collections.Generic.Dictionary<'id, float>(capacity)

            for id in candidates do
                let score =
                    postings
                    |> List.fold
                        (fun state posting ->
                            Map.tryFind id posting.Rows
                            |> Option.map (fun frequency -> float frequency * posting.Scale)
                            |> fun contribution -> addBooleanContribution posting.Operator contribution state)
                        emptyBooleanScore
                    |> tryBooleanScore

                score |> Option.iter (fun score -> scores.Add(id, visibleBooleanScore score))

            scores

        let accumulatePositivePostings () =
            let capacity =
                positiveTerms
                |> List.sumBy (fun posting -> int64 posting.Rows.Count)
                |> min (int64 view.DocumentCount)
                |> int

            let totals = Collections.Generic.Dictionary<'id, float>(capacity)
            let inScope id = candidateIds |> Option.forall (fun candidates -> candidates.Contains id)

            for posting in positiveTerms do
                for KeyValue(id, frequency) in posting.Rows do
                    if inScope id then
                        let contribution = float frequency * posting.Scale

                        let updatedScore current =
                            match posting.Operator with
                            | Raise -> current + contribution + 1.0
                            | Lower -> current + contribution - 1.0
                            | Soft -> current
                            | _ -> current + contribution

                        match totals.TryGetValue id with
                        | true, current -> totals.[id] <- updatedScore current
                        | false, _ -> totals.Add(id, updatedScore 0.0)

            for posting in postings do
                if posting.Operator = MustNot then
                    for KeyValue(id, _) in posting.Rows do
                        totals.Remove id |> ignore

            let scores = Collections.Generic.Dictionary<'id, float>(totals.Count)

            totals.Keys
            |> Seq.sort
            |> Seq.iter (fun id ->
                let score = totals.[id]
                scores.Add(id, visibleBooleanScore score))

            scores

        match smallestRequiredPosting with
        | Some rows ->
            let candidates: seq<'id> =
                match candidateIds with
                | Some candidates when candidates.Count < rows.Count -> Set.toSeq candidates
                | Some candidates ->
                    rows
                    |> Map.toSeq
                    |> Seq.map fst
                    |> Seq.filter candidates.Contains
                | None -> rows |> Map.toSeq |> Seq.map fst

            let capacity = candidateIds |> Option.map _.Count |> Option.defaultValue rows.Count |> min rows.Count
            scoreCandidates candidates capacity
        | None ->
            let candidateProbeWork =
                candidateIds
                |> Option.map (fun candidates -> int64 candidates.Count * int64 postings.Length)

            let postingWork =
                positiveTerms
                |> List.sumBy (fun posting -> int64 posting.Rows.Count)

            match candidateIds, candidateProbeWork with
            | Some candidates, Some work when work < postingWork -> scoreCandidates candidates candidates.Count
            | _ -> accumulatePositivePostings ())

let internal tryFlatBooleanScoresDictionaryWithin candidateIds index query =
    tryFlatBooleanScoresDictionaryInView candidateIds (readView index) query

let internal tryFlatBooleanScoresDictionary index query =
    tryFlatBooleanScoresDictionaryWithin None index query

let booleanScoresOf (corpus: Corpus) (query: string) : float[] =
    // A matched row whose contributions all cancelled (only `~` terms hit,
    // or everywhere-present words at the floor) still has to read as a
    // match in a WHERE clause — the same epsilon rank the floor gives.
    booleanScores corpus.Index query |> scoresInCorpusOrder corpus

// ---------------------------------------------------------------------------
// Query expansion: NL pass, expand the query with every searchable token of
// the top-ranked docs, NL pass again (blind relevance feedback).
// ---------------------------------------------------------------------------

let internal expansionScoresInView candidateIds (view: ReadView<'id>) (query: string) =
    let candidateIds = candidatesInView candidateIds view
    let clauseQuery =
        if needsNaturalWordEvaluation view query then
            let clauses = naturalWordClauses view query
            Some(clauses, naturalWordMatches view.VisibleDocumentIds view clauses)
        else
            None

    let firstPass =
        match clauseQuery with
        | Some(_, matches) -> scoreNaturalWordMatches view matches
        | None -> naturalScoresInView None view query

    let seedTerms =
        firstPass
        |> Map.toArray
        |> Array.sortByDescending snd
        |> Array.truncate queryExpansionLimit
        |> Array.collect (fun (id, _) ->
            let document = view.Documents.[id]
            Array.concat document.Fields |> Array.filter (isSearchable document.Rules))
        |> Array.map _.Key

    match clauseQuery with
    | Some(clauses, matches) ->
        // Only terms that contributed to the first pass are already searched.
        let expansion =
            seedTerms |> Array.distinct
            |> Array.filter (fun key -> not (Map.containsKey key matches.Documents))
            |> Array.map NaturalWord
        Array.append clauses expansion
        |> naturalWordMatches candidateIds view
        |> scoreNaturalWordMatches view
    | None ->
        Array.append (naturalTerms view query) seedTerms
        |> Array.distinct
        |> sumTermScoresWithin candidateIds view

let expansionScores (index: Index<'id>) (query: string) : Map<'id, float> =
    expansionScoresInView None (readView index) query

let internal expansionScoresWithin (candidateIds: Set<'id>) (index: Index<'id>) (query: string) =
    expansionScoresInView (Some candidateIds) (readView index) query

let expansionScoresOf (corpus: Corpus) (query: string) : float[] =
    expansionScores corpus.Index query |> scoresInCorpusOrder corpus
