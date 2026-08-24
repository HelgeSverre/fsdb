/// Full-text scoring for `MATCH (cols) AGAINST (...)` — natural language,
/// boolean, and query-expansion modes over an immutable inverted index (one
/// concatenated document per row).
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

/// `@@innodb_ft_min_token_size`'s default — shorter tokens are never
/// indexed or searched (fixed, not a knob; same stance as the other
/// `innodb_ft_*` tunables this module hardcodes).
let minTokenLength = 3

/// `@@ft_query_expansion_limit`'s default: how many top-ranked documents
/// seed the second pass of WITH QUERY EXPANSION.
let private queryExpansionLimit = 20

/// InnoDB's default stopword list, verbatim from a live 8.4.11's
/// `INFORMATION_SCHEMA.INNODB_FT_DEFAULT_STOPWORD` (36 rows; "the" really
/// does appear twice there).
let private stopwords =
    Set.ofList
        [ "a"; "about"; "an"; "are"; "as"; "at"; "be"; "by"; "com"; "de"
          "en"; "for"; "from"; "how"; "i"; "in"; "is"; "it"; "la"; "of"
          "on"; "or"; "that"; "the"; "this"; "to"; "was"; "what"; "when"
          "where"; "who"; "will"; "with"; "und"; "www" ]

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

let tokenize (text: string) : string[] = rawTokens text |> Array.map _.ToLowerInvariant()

// ---------------------------------------------------------------------------
// Corpus: rows tokenized once per MATCH evaluation.
// ---------------------------------------------------------------------------

type private Token =
    { Text: string
      Key: string }

type Index<'id when 'id: comparison> =
    private
        { Documents: Map<'id, Token[]>
          Postings: Map<string, Map<'id, int>>
          Vocabulary: Map<string, string>
          Collation: Collation }

type Corpus =
    private
        { Order: int[]
          Index: Index<int> }

let private tokensWith (collation: Collation) (text: string) =
    let token text =
        { Text = text
          Key = collation.KeyOf text }

    rawTokens text |> Array.map token

let emptyIndex (collation: Collation) : Index<'id> =
    { Documents = Map.empty
      Postings = Map.empty
      Vocabulary = Map.empty
      Collation = collation }

let removeDocument (id: 'id) (index: Index<'id>) : Index<'id> =
    match Map.tryFind id index.Documents with
    | None -> index
    | Some tokens ->
        let frequencies = tokens |> Array.countBy _.Key

        let postings =
            frequencies
            |> Array.fold
                (fun postings (key, _) ->
                    match Map.tryFind key postings with
                    | None -> postings
                    | Some rows ->
                        let remaining = Map.remove id rows
                        if remaining.IsEmpty then Map.remove key postings else Map.add key remaining postings)
                index.Postings

        let vocabulary =
            frequencies
            |> Array.fold
                (fun vocabulary (key, _) ->
                    if Map.containsKey key postings then vocabulary else Map.remove key vocabulary)
                index.Vocabulary

        { index with
            Documents = Map.remove id index.Documents
            Postings = postings
            Vocabulary = vocabulary }

let addDocument (id: 'id) (text: string) (index: Index<'id>) : Index<'id> =
    let index = removeDocument id index
    let tokens = tokensWith index.Collation text

    let postings, vocabulary =
        tokens
        |> Array.groupBy _.Key
        |> Array.fold
            (fun (postings, vocabulary) (key, tokens) ->
                let rows = postings |> Map.tryFind key |> Option.defaultValue Map.empty
                Map.add key (Map.add id tokens.Length rows) postings,
                Map.add key tokens.[0].Text vocabulary)
            (index.Postings, index.Vocabulary)

    { index with
        Documents = Map.add id tokens index.Documents
        Postings = postings
        Vocabulary = vocabulary }

let buildIndexWith (collation: Collation) (documents: ('id * string) seq) : Index<'id> =
    documents |> Seq.fold (fun index (id, text) -> addDocument id text index) (emptyIndex collation)

let buildCorpusWith (collation: Collation) (docs: string seq) : Corpus =
    let documents = docs |> Seq.indexed |> Array.ofSeq
    { Order = documents |> Array.map fst
      Index = buildIndexWith collation documents }

let buildCorpus (docs: string seq) : Corpus =
    buildCorpusWith defaultCollation docs

/// A token that survives the min-length and stopword rules — what both the
/// index side and a query's plain terms are reduced to.
let private isSearchable (token: Token) =
    token.Text.Length >= minTokenLength
    && not (Set.contains (token.Text.ToLowerInvariant()) stopwords)

let private idf (index: Index<'id>) (df: int) : float =
    if df = 0 then 0.0
    else max (log10 (float index.Documents.Count / float df)) idfFloor

let private termScores (index: Index<'id>) (term: string) : Map<'id, float> =
    match Map.tryFind term index.Postings with
    | None -> Map.empty
    | Some rows ->
        let weight = idf index rows.Count
        rows |> Map.map (fun _ frequency -> float frequency * weight * weight)

// ---------------------------------------------------------------------------
// Natural language mode.
// ---------------------------------------------------------------------------

/// Distinct searchable terms of a natural-language query.
let private queryTokens (index: Index<'id>) (query: string) =
    rawTokens query
    |> Array.map (fun text ->
        { Text = text
          Key = index.Collation.KeyOf text })

let private naturalTerms (index: Index<'id>) (query: string) : string[] =
    queryTokens index query
    |> Array.filter isSearchable
    |> Array.map _.Key
    |> Array.distinct

/// Element-wise sum of every term's per-doc contribution — the natural and
/// query-expansion modes are both exactly this over different term sets.
/// Accumulates into one result array in place rather than mapping every term
/// to its own row array first, so peak memory stays O(rows), not
/// O(terms × rows) for a query with many distinct terms.
let private sumTermScores (index: Index<'id>) (terms: string[]) : Map<'id, float> =
    terms
    |> Array.fold
        (fun scores term ->
            termScores index term
            |> Map.fold (fun scores id score -> Map.change id (Some << ((+) score) << Option.defaultValue 0.0) scores) scores)
        Map.empty

let naturalScores (index: Index<'id>) (query: string) : Map<'id, float> =
    sumTermScores index (naturalTerms index query)

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
// manual's "lowers" is MyISAM's older behavior). ponytail: `@N` proximity
// is "all quoted words within an N-token window", the common reading; the
// manual doesn't pin the exact distance definition.
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

let private parseBooleanQuery (collation: Collation) (query: string) : (BoolOp * BoolTerm) list =
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
        { Text = text
          Key = collation.KeyOf text }

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

                    let words =
                        rawTokens phrase
                        |> Array.map (fun text ->
                            { Text = text
                              Key = collation.KeyOf text })

                    acc.Add(op, BPhrase(words, proximity))
                elif isWordChar query.[i] then
                    let w = readWord ()
                    let prefix = i < len && query.[i] = '*'
                    if prefix then i <- i + 1
                    if w.Text.Length > 0 then acc.Add(op, BWord(w, prefix))
                else
                    // Punctuation MySQL's parser ignores.
                    i <- i + 1

        List.ofSeq acc

    nodes 0 false

/// Whether `doc` contains the quoted words as adjacent tokens in order
/// (`proximity = None`) or all within an (N+1)-token window (`Some N`).
/// Returns the occurrence count (phrase TF).
let private phraseCount (doc: Token[]) (words: Token[]) (proximity: int option) : int =
    if words.Length = 0 then
        0
    else
        match proximity with
        | None ->
            let mutable count = 0
            for start in 0 .. doc.Length - words.Length do
                let mutable ok = true
                for j in 0 .. words.Length - 1 do
                    if doc.[start + j].Key <> words.[j].Key then ok <- false
                if ok then count <- count + 1
            count
        | Some dist ->
            // All words present with positions spanning at most `dist`.
            // ponytail: the window search is exponential in the phrase's
            // word count over each word's occurrence list — fine for the
            // short quoted phrases proximity is used with; make it a sliding
            // window if anyone feeds it a paragraph.
            let positions =
                words
                |> Array.map (fun word ->
                    doc
                    |> Array.mapi (fun i token -> i, token.Key)
                    |> Array.filter (snd >> (=) word.Key)
                    |> Array.map fst)
            if positions |> Array.exists Array.isEmpty then
                0
            else
                // Smallest window containing one position of each word.
                let found =
                    positions.[0]
                    |> Array.exists (fun p0 ->
                        let rec fits (k: int) (lo: int) (hi: int) =
                            if k = positions.Length then hi - lo <= dist
                            else positions.[k] |> Array.exists (fun p -> fits (k + 1) (min lo p) (max hi p))
                        fits 1 p0 p0)
                if found then 1 else 0

/// `(matched, TF×IDF²)` per doc from raw per-doc frequencies — for terms
/// with no single index token to count (prefix wildcards, phrases), whose
/// df falls out of the frequencies themselves.
let private scoresFromTfs (index: Index<'id>) (tfs: Map<'id, int>) : Map<'id, bool * float> =
    let weight = idf index tfs.Count
    tfs |> Map.map (fun _ tf -> true, float tf * weight * weight)

let private allDocuments (index: Index<'id>) (scores: Map<'id, bool * float>) =
    index.Documents
    |> Map.map (fun id _ -> scores |> Map.tryFind id |> Option.defaultValue (false, 0.0))

let private phraseCandidates (index: Index<'id>) (words: Token[]) =
    words
    |> Array.map (fun word ->
        index.Postings
        |> Map.tryFind word.Key
        |> Option.map (Map.keys >> Set.ofSeq)
        |> Option.defaultValue Set.empty)
    |> Array.sortBy _.Count
    |> function
        | [||] -> Set.empty
        | sets -> sets |> Array.tail |> Array.fold Set.intersect sets.[0]

/// Per-doc (matched, contribution) for one boolean term.
let rec private evalTerm (index: Index<'id>) (term: BoolTerm) : Map<'id, bool * float> =
    match term with
    | BWord(term, false) when not (isSearchable term) ->
        // Stopwords and sub-minimum tokens are never in InnoDB's index, so
        // a plain boolean term for one can't match anything — `+was`
        // excludes every row (oracle-verified). Phrases and proximity below
        // still see them: position data counts every token.
        Map.empty |> allDocuments index
    | BWord(term, false) ->
        termScores index term.Key
        |> Map.map (fun _ score -> true, score)
        |> allDocuments index
    | BWord(term, true) ->
        // Prefix wildcards bypass stopword and minimum-length rules.
        index.Vocabulary
        |> Map.toSeq
        |> Seq.choose (fun (key, text) -> if index.Collation.IsPrefix text term.Text then Map.tryFind key index.Postings else None)
        |> Seq.fold
            (fun frequencies posting ->
                posting
                |> Map.fold
                    (fun frequencies id frequency ->
                        Map.change id (fun current -> Some(frequency + Option.defaultValue 0 current)) frequencies)
                    frequencies)
            Map.empty
        |> scoresFromTfs index
        |> allDocuments index
    | BPhrase(words, proximity) ->
        phraseCandidates index words
        |> Seq.choose (fun id ->
            let count = phraseCount index.Documents.[id] words proximity
            if count = 0 then None else Some(id, count))
        |> Map.ofSeq
        |> scoresFromTfs index
        |> allDocuments index
    | BGroup nodes ->
        evalNodes index nodes

/// Per-doc (matched, score) over a node list — the boolean combination:
/// a doc is excluded (matched=false) when a `+` term misses or a `-` term
/// hits; otherwise matched when anything matched, scoring the sum of the
/// modifier-adjusted contributions.
and private evalNodes (index: Index<'id>) (nodes: (BoolOp * BoolTerm) list) : Map<'id, bool * float> =
    let results = nodes |> List.map (fun (op, term) -> op, evalTerm index term)

    index.Documents
    |> Map.map (fun id _ ->
        let mutable excluded = false
        let mutable anyMatch = false
        let mutable score = 0.0

        for op, r in results do
            let matched, s = r.[id]

            match op with
            | Must ->
                if matched then
                    anyMatch <- true
                    score <- score + s
                else
                    excluded <- true
            | MustNot -> if matched then excluded <- true
            | Optional ->
                if matched then
                    anyMatch <- true
                    score <- score + s
            | Raise ->
                if matched then
                    anyMatch <- true
                    score <- score + s + 1.0
            | Lower ->
                if matched then
                    anyMatch <- true
                    score <- score + s - 1.0
            | Soft -> if matched then anyMatch <- true

        (anyMatch && not excluded), (if excluded then 0.0 else score))

let booleanScores (index: Index<'id>) (query: string) : Map<'id, float> =
    evalNodes index (parseBooleanQuery index.Collation query)
    |> Map.fold
        (fun scores id (matched, score) ->
            if matched then Map.add id (if score = 0.0 then idfFloor * idfFloor else score) scores else scores)
        Map.empty

let booleanScoresOf (corpus: Corpus) (query: string) : float[] =
    // A matched row whose contributions all cancelled (only `~` terms hit,
    // or everywhere-present words at the floor) still has to read as a
    // match in a WHERE clause — the same epsilon rank the floor gives.
    booleanScores corpus.Index query |> scoresInCorpusOrder corpus

// ---------------------------------------------------------------------------
// Query expansion: NL pass, expand the query with every searchable token of
// the top-ranked docs, NL pass again (blind relevance feedback).
// ---------------------------------------------------------------------------

let expansionScores (index: Index<'id>) (query: string) : Map<'id, float> =
    let firstPass = naturalScores index query

    let seedTerms =
        firstPass
        |> Map.toArray
        |> Array.sortByDescending snd
        |> Array.truncate queryExpansionLimit
        |> Array.collect (fun (id, _) -> index.Documents.[id])
        |> Array.filter isSearchable
        |> Array.map _.Key

    Array.append (naturalTerms index query) seedTerms
    |> Array.distinct
    |> sumTermScores index

let expansionScoresOf (corpus: Corpus) (query: string) : float[] =
    expansionScores corpus.Index query |> scoresInCorpusOrder corpus
