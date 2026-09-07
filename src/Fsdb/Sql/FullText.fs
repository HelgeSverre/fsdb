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

/// `@@innodb_ft_max_token_size`'s default.
let maxTokenLength = 84

/// `@@ft_query_expansion_limit`'s default: how many top-ranked documents
/// seed the second pass of WITH QUERY EXPANSION.
let private queryExpansionLimit = 20

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

let tokenize (text: string) : string[] = rawTokens text |> Array.map _.ToLowerInvariant()

// ---------------------------------------------------------------------------
// Corpus compatibility wrapper over the immutable index.
// ---------------------------------------------------------------------------

type private Token =
    { Text: string
      Key: string }

type Index<'id when 'id: comparison> =
    private
        { Documents: Map<'id, Token[]>
          Postings: Map<string, Map<'id, int>>
          PrefixPostings: Map<string, Map<'id, int>>
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

/// A token that survives the configured length and stopword rules.
let private isSearchable (token: Token) =
    token.Text.Length >= minTokenLength
    && token.Text.Length <= maxTokenLength
    && not (Set.contains (token.Text.ToLowerInvariant()) stopwords)

let private prefixKeys (collation: Collation) (token: Token) =
    [| 1..token.Text.Length |]
    |> Array.map (fun length -> collation.KeyOf(token.Text.Substring(0, length)))
    |> Array.distinct

let private removePosting id key postings =
    match Map.tryFind key postings with
    | None -> postings
    | Some rows ->
        let remaining = Map.remove id rows
        if remaining.IsEmpty then Map.remove key postings else Map.add key remaining postings

let emptyIndex (collation: Collation) : Index<'id> =
    { Documents = Map.empty
      Postings = Map.empty
      PrefixPostings = Map.empty
      Collation = collation }

let removeDocument (id: 'id) (index: Index<'id>) : Index<'id> =
    match Map.tryFind id index.Documents with
    | None -> index
    | Some tokens ->
        let frequencies = tokens |> Array.countBy _.Key
        let prefixes =
            tokens
            |> Array.filter isSearchable
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

let addDocument (id: 'id) (text: string) (index: Index<'id>) : Index<'id> =
    let index = removeDocument id index
    let tokens = tokensWith index.Collation text

    let postings =
        tokens
        |> Array.groupBy _.Key
        |> Array.fold
            (fun postings (key, tokens) ->
                let rows = postings |> Map.tryFind key |> Option.defaultValue Map.empty
                Map.add key (Map.add id tokens.Length rows) postings)
            index.Postings

    let prefixPostings =
        tokens
        |> Array.filter isSearchable
        |> Array.collect (prefixKeys index.Collation)
        |> Array.countBy (fun key -> key)
        |> Array.fold
            (fun postings (key, frequency) ->
                let rows = postings |> Map.tryFind key |> Option.defaultValue Map.empty
                Map.add key (Map.add id frequency rows) postings)
            index.PrefixPostings

    { index with
        Documents = Map.add id tokens index.Documents
        Postings = postings
        PrefixPostings = prefixPostings }

let buildIndexWith (collation: Collation) (documents: ('id * string) seq) : Index<'id> =
    documents |> Seq.fold (fun index (id, text) -> addDocument id text index) (emptyIndex collation)

let buildCorpusWith (collation: Collation) (docs: string seq) : Corpus =
    let documents = docs |> Seq.indexed |> Array.ofSeq
    { Order = documents |> Array.map fst
      Index = buildIndexWith collation documents }

let buildCorpus (docs: string seq) : Corpus =
    buildCorpusWith defaultCollation docs

let private idf (index: Index<'id>) (df: int) : float =
    if df = 0 then 0.0
    else max (log10 (float index.Documents.Count / float df)) idfFloor

let private termScoresWithin (candidateIds: Set<'id> option) (index: Index<'id>) (term: string) : Map<'id, float> =
    match Map.tryFind term index.Postings with
    | None -> Map.empty
    | Some rows ->
        let weight = idf index rows.Count
        let scale = weight * weight

        match candidateIds with
        | None -> rows |> Map.map (fun _ frequency -> float frequency * scale)
        | Some candidates ->
            candidates
            |> Seq.choose (fun id -> Map.tryFind id rows |> Option.map (fun frequency -> id, float frequency * scale))
            |> Map.ofSeq

let private termScores index term = termScoresWithin None index term

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
/// Accumulates into one result map rather than retaining one map per term,
/// so peak memory stays O(rows), not
/// O(terms × rows) for a query with many distinct terms.
let private sumTermScoresWithin (candidateIds: Set<'id> option) (index: Index<'id>) (terms: string[]) : Map<'id, float> =
    match terms with
    | [||] -> Map.empty
    | [| term |] -> termScoresWithin candidateIds index term
    | _ ->
        terms
        |> Array.skip 1
        |> Array.fold
            (fun scores term ->
                termScoresWithin candidateIds index term
                |> Map.fold
                    (fun scores id score ->
                        Map.change id (fun current -> Some(score + Option.defaultValue 0.0 current)) scores)
                    scores)
            (termScoresWithin candidateIds index terms.[0])

let private sumTermScores index terms = sumTermScoresWithin None index terms

let naturalScores (index: Index<'id>) (query: string) : Map<'id, float> =
    sumTermScores index (naturalTerms index query)

let internal naturalScoresWithin (candidateIds: Set<'id>) (index: Index<'id>) (query: string) : Map<'id, float> =
    sumTermScoresWithin (Some candidateIds) index (naturalTerms index query)

let internal tryNaturalSingleTermScoresDictionaryWithin
    (candidateIds: Set<'id> option)
    (index: Index<'id>)
    (query: string)
    : Collections.Generic.Dictionary<'id, float> option =
    match naturalTerms index query with
    | [| term |] ->
        match Map.tryFind term index.Postings with
        | None -> Collections.Generic.Dictionary() |> Some
        | Some rows ->
            let weight = idf index rows.Count
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
// manual's "lowers" is MyISAM's older behavior). `@N` proximity
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

type private FlatPosting<'id when 'id: comparison> =
    { Operator: BoolOp
      Rows: Map<'id, int>
      Scale: float }

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
                        |> Array.skipWhile (isSearchable >> not)

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

let private exactPhraseMatches (doc: Token[]) (words: Token[]) =
    if words.Length = 0 then
        false
    else
        seq { 0 .. doc.Length - words.Length }
        |> Seq.exists (fun start ->
            words
            |> Array.indexed
            |> Array.forall (fun (offset, word) -> doc.[start + offset].Key = word.Key))

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
    (index: Index<'id>)
    (documentFrequency: int)
    (tfs: Map<'id, int>)
    : Map<'id, float> =
    let weight = idf index documentFrequency
    tfs |> Map.map (fun _ tf -> float tf * weight * weight)

let private restrictScores (candidateIds: Set<'id> option) (scores: Map<'id, 'value>) =
    match candidateIds with
    | None -> scores
    | Some candidates ->
        candidates
        |> Seq.choose (fun id -> Map.tryFind id scores |> Option.map (fun score -> id, score))
        |> Map.ofSeq

let private phraseCandidates (index: Index<'id>) (words: Token[]) =
    words
    // InnoDB omits short terms and stopwords from postings but retains their
    // positions after the first searchable word in a phrase.
    |> Array.filter isSearchable
    |> Array.distinctBy _.Key
    |> Array.map (fun word ->
        index.Postings
        |> Map.tryFind word.Key
        |> Option.map (Map.keys >> Set.ofSeq)
        |> Option.defaultValue Set.empty)
    |> Array.sortBy _.Count
    |> function
        | [||] -> Set.empty
        | sets -> sets |> Array.tail |> Array.fold Set.intersect sets.[0]

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
    (index: Index<'id>)
    (term: BoolTerm)
    : Map<'id, float> =
    match term with
    | BWord(term, false) when not (isSearchable term) ->
        // Stopwords and sub-minimum tokens are never in InnoDB's index, so
        // a plain boolean term for one can't match anything — `+was`
        // excludes every row (oracle-verified). Phrases and proximity below
        // still see them: position data counts every token.
        Map.empty
    | BWord(term, false) ->
        termScoresWithin candidateIds index term.Key
    | BWord(term, true) ->
        // Prefix wildcards bypass stopword and minimum-length rules.
        let rows =
            index.PrefixPostings
            |> Map.tryFind term.Key
            |> Option.defaultValue Map.empty

        rows
        |> restrictScores candidateIds
        |> scoresFromTfsWithDocumentFrequency index rows.Count
    | BPhrase(words, proximity) ->
        let candidates =
            phraseCandidates index words
            |> fun candidates -> candidateIds |> Option.map (Set.intersect candidates) |> Option.defaultValue candidates

        let terms =
            words
            |> Array.filter isSearchable
            |> Array.distinctBy _.Key
            |> Array.choose (fun word ->
                index.Postings
                |> Map.tryFind word.Key
                |> Option.map (fun rows ->
                    let weight = idf index rows.Count
                    rows, weight * weight))

        candidates
        |> Seq.choose (fun id ->
            let document = index.Documents.[id]
            let matches =
                match proximity with
                | None -> exactPhraseMatches document words
                | Some distance -> proximityMatches document words distance

            if matches then
                terms
                |> Array.sumBy (fun (rows, scale) -> float rows.[id] * scale)
                |> fun score -> Some(id, score)
            else
                None)
        |> Map.ofSeq
    | BGroup nodes ->
        evalNodes candidateIds index nodes

/// Per-document score over a node list — the boolean combination:
/// a document is absent when a `+` term misses or a `-` term
/// hits; otherwise matched when anything matched, scoring the sum of the
/// modifier-adjusted contributions.
and private evalNodes
    (candidateIds: Set<'id> option)
    (index: Index<'id>)
    (nodes: (BoolOp * BoolTerm) list)
    : Map<'id, float> =
    let results = nodes |> List.map (fun (op, term) -> op, evalTerm candidateIds index term)

    booleanCandidates results
    |> Seq.choose (fun id ->
        let mutable excluded = false
        let mutable anyMatch = false
        let mutable score = 0.0

        for op, r in results do
            let contribution = Map.tryFind id r
            let matched = contribution.IsSome
            let contribution = Option.defaultValue 0.0 contribution

            match op with
            | Must ->
                if matched then
                    anyMatch <- true
                    score <- score + contribution
                else
                    excluded <- true
            | MustNot -> if matched then excluded <- true
            | Optional ->
                if matched then
                    anyMatch <- true
                    score <- score + contribution
            | Raise ->
                if matched then
                    anyMatch <- true
                    score <- score + contribution + 1.0
            | Lower ->
                if matched then
                    anyMatch <- true
                    score <- score + contribution - 1.0
            | Soft -> if matched then anyMatch <- true

        if anyMatch && not excluded then Some(id, score) else None)
    |> Map.ofSeq

let private visibleBooleanScore score =
    if score = 0.0 then idfFloor * idfFloor else score

let private booleanScoresWithinOption candidateIds (index: Index<'id>) (query: string) =
    evalNodes candidateIds index (parseBooleanQuery index.Collation query)
    |> Map.map (fun _ score -> visibleBooleanScore score)

let booleanScores (index: Index<'id>) (query: string) : Map<'id, float> =
    booleanScoresWithinOption None index query

let internal booleanScoresWithin (candidateIds: Set<'id>) (index: Index<'id>) (query: string) =
    booleanScoresWithinOption (Some candidateIds) index query

let internal tryFlatBooleanScoresDictionaryWithin
    (candidateIds: Set<'id> option)
    (index: Index<'id>)
    (query: string)
    : Collections.Generic.Dictionary<'id, float> option =
    let rec flatTerms (found: (BoolOp * Token * bool) list) =
        function
        | [] when not found.IsEmpty -> Some(List.rev found)
        | (op, BWord(term, prefix)) :: rest -> flatTerms ((op, term, prefix) :: found) rest
        | _ -> None

    parseBooleanQuery index.Collation query
    |> flatTerms []
    |> Option.map (fun terms ->
        let postingFor (term: Token, prefix) =
            if prefix then Map.tryFind term.Key index.PrefixPostings
            elif isSearchable term then Map.tryFind term.Key index.Postings
            else None

        let postings =
            terms
            |> List.map (fun (op, term, prefix) ->
                let rows = postingFor (term, prefix) |> Option.defaultValue Map.empty
                let weight = idf index rows.Count
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
                let mutable excluded = false
                let mutable anyMatch = false
                let mutable score = 0.0

                for posting in postings do
                    let frequency = Map.tryFind id posting.Rows
                    let matched = frequency.IsSome
                    let contribution =
                        frequency
                        |> Option.map (fun value -> float value * posting.Scale)
                        |> Option.defaultValue 0.0

                    match posting.Operator with
                    | Must ->
                        if matched then
                            anyMatch <- true
                            score <- score + contribution
                        else
                            excluded <- true
                    | MustNot -> if matched then excluded <- true
                    | Optional ->
                        if matched then
                            anyMatch <- true
                            score <- score + contribution
                    | Raise ->
                        if matched then
                            anyMatch <- true
                            score <- score + contribution + 1.0
                    | Lower ->
                        if matched then
                            anyMatch <- true
                            score <- score + contribution - 1.0
                    | Soft -> if matched then anyMatch <- true

                if anyMatch && not excluded then
                    scores.Add(id, visibleBooleanScore score)

            scores

        let accumulatePositivePostings () =
            let capacity =
                positiveTerms
                |> List.sumBy (fun posting -> int64 posting.Rows.Count)
                |> min (int64 index.Documents.Count)
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

let private expansionScoresWithinOption candidateIds (index: Index<'id>) (query: string) =
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
    |> sumTermScoresWithin candidateIds index

let expansionScores (index: Index<'id>) (query: string) : Map<'id, float> =
    expansionScoresWithinOption None index query

let internal expansionScoresWithin (candidateIds: Set<'id>) (index: Index<'id>) (query: string) =
    expansionScoresWithinOption (Some candidateIds) index query

let expansionScoresOf (corpus: Corpus) (query: string) : float[] =
    expansionScores corpus.Index query |> scoresInCorpusOrder corpus
