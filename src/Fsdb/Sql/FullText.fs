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

/// Default minimum indexed word length; ngram indexes use their configured token size.
let minTokenLength = StorageOptions.defaultWordLengths.Minimum

/// `@@innodb_ft_max_token_size`'s default.
let maxTokenLength = StorageOptions.defaultWordLengths.Maximum

let ngramTokenSize = StorageOptions.defaults.NgramTokenSize

type Tokenizer =
    | Words of StorageOptions.WordLengths
    | Ngrams of size: int

let defaultTokenizer = Words StorageOptions.defaultWordLengths

type StopwordList =
    private
        { CollationName: string
          Words: Set<string>
          Keys: Set<string> }

[<RequireQualifiedAccess>]
type StopwordPolicy =
    | BuiltIn
    | Disabled
    | Custom of StopwordList

type StopwordSettings =
    { Source: string option
      Policy: StopwordPolicy }

let internal customStopwords (collation: Collation) words =
    let words = words |> Seq.filter (String.IsNullOrEmpty >> not) |> Set.ofSeq
    StopwordPolicy.Custom
        { CollationName = collation.Name
          Words = words
          Keys = words |> Seq.map collation.FullTextKeyOf |> Set.ofSeq }

let internal customStopwordContents words = words.CollationName, Set.toList words.Words

/// Rules captured by a document when its postings are built.
type internal IndexingRules =
    { Tokenizer: Tokenizer
      Stopwords: StopwordPolicy }

let tryTokenizer = function
    | None -> Some defaultTokenizer
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
    Char.IsLetter c
    || Char.IsNumber c
    || c = '_'
    || (match Globalization.CharUnicodeInfo.GetUnicodeCategory c with
        | Globalization.UnicodeCategory.NonSpacingMark
        | Globalization.UnicodeCategory.SpacingCombiningMark
        | Globalization.UnicodeCategory.EnclosingMark -> true
        | _ -> false)

let private rawTokensBy isWord keepApostrophes (text: string) : string[] =
    let tokens = ResizeArray<string>()
    let current = Text.StringBuilder()

    let flush () =
        let text = current.ToString()
        let t = if keepApostrophes then text.Trim('\'') else text
        if t.Length > 0 then tokens.Add t
        current.Clear() |> ignore

    for c in text do
        if isWord c then current.Append c |> ignore
        elif keepApostrophes && c = '\'' && current.Length > 0 then current.Append c |> ignore
        else flush ()

    flush ()
    tokens.ToArray()

let private rawTokens = rawTokensBy isWordChar true

let private ngrams size (text: string) =
    let boundary (rune: Text.Rune) =
        rune.IsAscii && not (Text.Rune.IsLetterOrDigit rune || rune.Value = int '_')

    text.EnumerateRunes()
    |> Seq.toArray
    |> Array.windowed size
    |> Array.choose (fun runes ->
        if Array.exists boundary runes then None
        else runes |> Array.map string |> String.concat "" |> Some)

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

[<Struct>]
type private IndexedDocument =
    { Content: Document
      Order: int64 }

type private PrefixPosting<'id when 'id: comparison> =
    { Rows: Map<'id, int>
      GramDocumentCount: int64 }

type Index<'id when 'id: comparison> =
    private
        { Documents: Map<'id, IndexedDocument>
          Postings: Map<string, Map<'id, int>>
          PrefixPostings: Map<string, PrefixPosting<'id>>
          Collation: Collation
          Rules: IndexingRules
          StopwordSource: string option
          StopwordReload: Lazy<StopwordPolicy> option
          NextDocumentOrder: int64
          DocumentOrderFollowsIds: bool
          HasDocumentOrder: bool }

/// Inspects cached rules without loading a remembered stopword source.
let internal storedRules (index: Index<'id>) =
    match index.StopwordReload with
    | Some reload when reload.IsValueCreated -> { index.Rules with Stopwords = reload.Value }
    | _ -> index.Rules

let internal activeRules (index: Index<'id>) =
    match index.StopwordReload with
    | Some reload -> { index.Rules with Stopwords = reload.Value }
    | None -> index.Rules

let internal loadedStopwords (index: Index<'id>) =
    match index.StopwordReload with
    | Some reload when not reload.IsValueCreated -> None
    | _ -> Some (storedRules index).Stopwords

let internal deferStopwords reload (index: Index<'id>) =
    { index with StopwordReload = Some reload }

/// Query state is separate from the index maintained for future commits.
type internal ReadView<'id when 'id: comparison> =
    private
        { Documents: Map<'id, IndexedDocument>
          Postings: Map<string, Map<'id, int>>
          PrefixPostings: Map<string, PrefixPosting<'id>>
          Collation: Collation
          Rules: IndexingRules
          DocumentCount: int
          VisibleDocumentIds: Set<'id> option
          DocumentOrderFollowsIds: bool
          HasDocumentOrder: bool }

let internal readView (index: Index<'id>) : ReadView<'id> =
    { Documents = index.Documents
      Postings = index.Postings
      PrefixPostings = index.PrefixPostings
      Collation = index.Collation
      Rules = activeRules index
      DocumentCount = index.Documents.Count
      VisibleDocumentIds = None
      DocumentOrderFollowsIds = index.DocumentOrderFollowsIds
      HasDocumentOrder = index.HasDocumentOrder }

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

let private isBinaryCollation (collation: Collation) =
    collation.Name = "binary" || collation.Name.EndsWith("_bin", StringComparison.Ordinal)

let private usesUtf8WordParser tokenizer (collation: Collation) =
    match tokenizer with
    | Words _ -> collation.Name.StartsWith("utf8", StringComparison.Ordinal)
    | Ngrams _ -> false

let private foldFullText (collation: Collation) text =
    if isBinaryCollation collation then text
    else Collation.lowercaseForFullText collation.Name text

let private tokenOfFolded (collation: Collation) text =
    { Text = text
      Key = collation.FullTextKeyOf text }

let private tokenWith collation text =
    // InnoDB folds full-text case even for nonbinary case-sensitive collations.
    foldFullText collation text |> tokenOfFolded collation

let private rawTokensWith tokenizer collation text =
    match tokenizer with
    | Words _ when usesUtf8WordParser tokenizer collation -> rawTokensBy BooleanWordChars.contains false text
    | Words _ -> rawTokens text
    | Ngrams size -> ngrams size text

let private tokensWith tokenizer collation text =
    rawTokensWith tokenizer collation text |> Array.map (tokenWith collation)

let private runeLength (text: string) = text.EnumerateRunes() |> Seq.length

let private customStopwordMatcher words =
    let collation = Collation.tryFind words.CollationName |> Option.get
    fun text -> Set.contains (collation.FullTextKeyOf text) words.Keys

let private isStopword policy (text: string) =
    match policy with
    | StopwordPolicy.Disabled -> false
    | StopwordPolicy.BuiltIn -> Set.contains (text.ToLowerInvariant()) stopwords
    | StopwordPolicy.Custom words -> customStopwordMatcher words text

let private containsNgramStopword policy (text: string) =
    match policy with
    | StopwordPolicy.Disabled -> false
    | StopwordPolicy.BuiltIn ->
        let lower = text.ToLowerInvariant()
        stopwords |> Set.exists (fun word -> lower.Contains(word, StringComparison.Ordinal))
    | StopwordPolicy.Custom words ->
        let matches = customStopwordMatcher words
        let runes = text.EnumerateRunes() |> Seq.map string |> Seq.toArray
        seq {
            for start in 0 .. runes.Length - 1 do
                for finish in start .. runes.Length - 1 do
                    yield String.Concat(runes.[start .. finish])
        }
        |> Seq.exists matches

let private hasSearchableWordLength (lengths: StorageOptions.WordLengths) (token: Token) =
    token.Text.Length >= lengths.Minimum && token.Text.Length <= lengths.Maximum

// Short query spellings can address longer indexed collation equivalents.
let private hasLookupWordLength (lengths: StorageOptions.WordLengths) (token: Token) = token.Text.Length <= lengths.Maximum

let private isSearchableWord lengths policy token =
    hasSearchableWordLength lengths token && not (isStopword policy token.Text)

/// A token that survives the configured length and stopword rules.
let private isSearchable rules (token: Token) =
    match rules.Tokenizer with
    | Words lengths -> isSearchableWord lengths rules.Stopwords token
    | Ngrams size ->
        runeLength token.Text = size && not (containsNgramStopword rules.Stopwords token.Text)

/// Boolean terms can address postings created with an earlier ngram size.
let private isBooleanSearchable rules token =
    match rules.Tokenizer with
    | Words _ -> isSearchable rules token
    | Ngrams _ -> not (containsNgramStopword rules.Stopwords token.Text)

/// Exact word lookups can reach older postings or an indexed collation equivalent.
let private isExactLookup rules token =
    match rules.Tokenizer with
    | Words lengths -> hasLookupWordLength lengths token
    | Ngrams _ -> isBooleanSearchable rules token

let private indexedTokens (document: Document) =
    Array.concat document.Fields |> Array.filter (isSearchable document.Rules)

let private prefixKeys (collation: Collation) (token: Token) =
    let mutable length = 0
    [| for rune in token.Text.EnumerateRunes() do
           length <- length + rune.Utf16SequenceLength
           collation.FullTextKeyOf(token.Text.Substring(0, length)) |]
    |> Array.distinct

let private documentPrefixes collation tokenizer tokens (words: (string * Token[])[]) =
    match tokenizer with
    | Words _ ->
        tokens
        |> Array.collect (prefixKeys collation)
        |> Array.countBy id
        |> Array.map (fun (key, frequency) -> key, (frequency, 0L))
    | Ngrams _ ->
        // InnoDB keeps the first matching gram's TF but tallies every gram's posting.
        let prefixes = Collections.Generic.Dictionary<string, int * int64>()

        for _, grams in Array.sortBy fst words do
            let keys =
                grams
                |> Array.distinctBy _.Text
                |> Array.collect (prefixKeys collation)
                |> Array.distinct

            for key in keys do
                match prefixes.TryGetValue key with
                | true, (frequency, count) -> prefixes.[key] <- frequency, count + 1L
                | false, _ -> prefixes.Add(key, (grams.Length, 1L))

        prefixes |> Seq.map (fun (KeyValue(key, counts)) -> key, counts) |> Seq.toArray

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
      Rules = { Tokenizer = tokenizer; Stopwords = policy }
      StopwordSource = None
      StopwordReload = None
      NextDocumentOrder = 1L
      DocumentOrderFollowsIds = true
      HasDocumentOrder = true }

let emptyIndex collation = emptyIndexWith StopwordPolicy.BuiltIn defaultTokenizer collation

let internal stopwordSource (index: Index<'id>) = index.StopwordSource

let internal withStopwordSource source (index: Index<'id>) =
    { index with StopwordSource = source }

let internal stopwordBuildSettings (index: Index<'id>) =
    // A cold table builds additional indexes before loading its remembered source.
    { Source = index.StopwordSource; Policy = loadedStopwords index |> Option.defaultValue StopwordPolicy.Disabled }

let internal withStopwordStateFrom (source: Index<'id>) (target: Index<'id>) =
    { target with
        StopwordSource = source.StopwordSource
        StopwordReload = source.StopwordReload }


/// Selects rules for queries and future writes, retaining each document's history.
let internal withIndexingRules rules (index: Index<'id>) =
    { index with Rules = rules; StopwordReload = None }

/// Selects the tokenizer for queries and future writes, retaining existing postings.
let internal withTokenizer tokenizer (index: Index<'id>) =
    { index with Rules = { index.Rules with Tokenizer = tokenizer } }

let internal activeTokenizer (index: Index<'id>) = index.Rules.Tokenizer

let internal withNgramTokenSize size (index: Index<'id>) =
    match index.Rules.Tokenizer with
    | Words _ -> index
    | Ngrams _ -> withTokenizer (Ngrams size) index

let internal withWordLengths lengths (index: Index<'id>) =
    match index.Rules.Tokenizer with
    | Words _ -> withTokenizer (Words lengths) index
    | Ngrams _ -> index

let removeDocument (id: 'id) (index: Index<'id>) : Index<'id> =
    match Map.tryFind id index.Documents with
    | None -> index
    | Some indexed ->
        let document = indexed.Content
        let tokens = indexedTokens document
        let words = tokens |> Array.groupBy _.Key
        let prefixes = documentPrefixes index.Collation document.Rules.Tokenizer tokens words

        let postings =
            words
            |> Array.fold (fun postings (key, _) -> removePosting id key postings) index.Postings

        let prefixPostings =
            prefixes
            |> Array.fold (fun postings (key, (_, gramCount)) ->
                postings
                |> Map.change key (Option.bind (fun posting ->
                    let remaining = Map.remove id posting.Rows
                    if remaining.IsEmpty then None
                    else Some { Rows = remaining; GramDocumentCount = posting.GramDocumentCount - gramCount })))
                index.PrefixPostings

        { index with
            Documents = Map.remove id index.Documents
            Postings = postings
            PrefixPostings = prefixPostings }

let private addDocumentData id (document: Document) (index: Index<'id>) =
    let index = removeDocument id index
    let postingTokens = indexedTokens document
    let words = postingTokens |> Array.groupBy _.Key

    let postings =
        words
        |> Array.fold
            (fun postings (key, tokens) ->
                let rows = postings |> Map.tryFind key |> Option.defaultValue Map.empty
                Map.add key (Map.add id tokens.Length rows) postings)
            index.Postings

    let prefixPostings =
        documentPrefixes index.Collation document.Rules.Tokenizer postingTokens words
        |> Array.fold
            (fun postings (key, (frequency, gramCount)) ->
                postings
                |> Map.change key (fun existing ->
                    let posting = existing |> Option.defaultValue { Rows = Map.empty; GramDocumentCount = 0L }
                    Some { Rows = Map.add id frequency posting.Rows
                           GramDocumentCount = posting.GramDocumentCount + gramCount }))
            index.PrefixPostings

    { index with
        Documents = Map.add id { Content = document; Order = index.NextDocumentOrder } index.Documents
        Postings = postings
        PrefixPostings = prefixPostings
        NextDocumentOrder = index.NextDocumentOrder + 1L
        HasDocumentOrder = index.HasDocumentOrder || index.Documents.IsEmpty
        DocumentOrderFollowsIds =
            index.Documents.IsEmpty
            || (index.DocumentOrderFollowsIds && id > fst (Map.maxKeyValue index.Documents)) }

let internal addDocumentFieldsWithRules rules id texts (index: Index<'id>) =
    // Phrase positions retain tokens omitted from ngram postings.
    let fields = texts |> List.map (tokensWith rules.Tokenizer index.Collation) |> List.toArray
    addDocumentData id { Fields = fields; Rules = rules } index

let internal sameDocument id (left: Index<'id>) (right: Index<'id>) =
    match Map.tryFind id left.Documents, Map.tryFind id right.Documents with
    | Some left, Some right -> obj.ReferenceEquals(left.Content, right.Content)
    | _ -> false

let internal mergeDocuments sourceRowIds (baseline: Index<'id>) (source: Index<'id>) (target: Index<'id>) =
    if obj.ReferenceEquals(baseline.Documents, source.Documents) then target
    else
        let targetId sourceId = Map.tryFind sourceId sourceRowIds |> Option.defaultValue sourceId
        let ids = Set.union (baseline.Documents |> Map.keys |> Set.ofSeq) (source.Documents |> Map.keys |> Set.ofSeq)
        let changes = ids |> Seq.filter (fun id -> not (sameDocument id baseline source))
                           |> Seq.map (fun id -> id, Map.tryFind id source.Documents)
                           |> Seq.toList
        let removed, written = changes |> List.partition (snd >> Option.isNone)
        let target = removed |> List.fold (fun index (id, _) -> removeDocument (targetId id) index) target
        written
        |> List.choose (fun (id, document) -> document |> Option.map (fun value -> targetId id, value))
        |> List.sortBy (snd >> _.Order)
        |> List.fold (fun index (id, document) -> addDocumentData id document.Content index) target

let addDocumentFields id texts (index: Index<'id>) =
    addDocumentFieldsWithRules (activeRules index) id texts index

let addDocument id text index = addDocumentFields id [ text ] index

let internal buildIndexWithStopwords policy tokenizer collation documents =
    documents
    |> Seq.fold (fun index (id, fields) -> addDocumentFields id fields index) (emptyIndexWith policy tokenizer collation)

let internal documentRules id (index: Index<'id>) =
    index.Documents |> Map.tryFind id |> Option.map (fun document -> document.Content.Rules)

let internal documentSnapshotState id (index: Index<'id>) =
    index.Documents |> Map.tryFind id
    |> Option.map (fun document ->
        document.Content.Rules, (if index.HasDocumentOrder then document.Order else 0L))

let internal restoreDocumentOrder order (index: Index<'id>) =
    if index.Documents.IsEmpty then index
    elif Map.isEmpty order || (order |> Map.forall (fun _ position -> position = 0L)) then
        // Older snapshots preserve postings, without their relative write order.
        { index with HasDocumentOrder = false }
    else
        let ordered =
            index.Documents
            |> Map.map (fun id document ->
                match Map.tryFind id order with
                | Some position when position > 0L && position < Int64.MaxValue -> { document with Order = position }
                | _ -> invalidOp "Persistence: invalid full-text document order")
        let positions = ordered |> Map.values |> Seq.map _.Order |> Seq.toArray
        if (Array.distinct positions).Length <> positions.Length then
            invalidOp "Persistence: repeated full-text document order"
        { index with
            Documents = ordered
            NextDocumentOrder = 1L + Array.max positions
            DocumentOrderFollowsIds = positions |> Array.pairwise |> Array.forall (fun (first, last) -> first < last) }

let internal withDocumentOrderFrom (source: Index<'id>) (target: Index<'id>) =
    { target with
        Documents = target.Documents |> Map.map (fun id document -> { document with Order = source.Documents.[id].Order })
        NextDocumentOrder = source.NextDocumentOrder
        DocumentOrderFollowsIds = source.DocumentOrderFollowsIds
        HasDocumentOrder = source.HasDocumentOrder }

let internal rulesInUse (index: Index<'id>) =
    seq {
        yield storedRules index
        for KeyValue(_, document) in index.Documents do
            yield document.Content.Rules
    }
    |> Seq.distinct
    |> Seq.toArray

let internal documentTokenizer id index =
    documentRules id index |> Option.map _.Tokenizer

/// ALTER replay uses the build policy even if a concurrent query has since loaded the source.
let internal constructionStopwords (index: Index<'id>) = index.Rules.Stopwords

let internal activeStopwords (index: Index<'id>) = (activeRules index).Stopwords

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

let buildIndexWith collation documents = buildIndexWithTokenizer defaultTokenizer collation documents

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

/// Short Boolean terms can reach historical postings, but not larger ngrams
/// whose collation keys happen to contract to the same spelling.
let private booleanPostingRows (view: ReadView<'id>) (token: Token) =
    view.Postings
    |> Map.tryFind token.Key
    |> Option.map (fun rows ->
        if view.Collation.Name <> "utf8mb4_general_ci" then rows
        else
            let length = runeLength token.Text
            rows
            |> Map.filter (fun id _ ->
                match view.Documents.[id].Content.Rules.Tokenizer with
                | Words _ -> true
                | Ngrams size -> length >= size))

let private exactPhraseMatches collation (doc: Token[]) (words: Token[]) =
    if words.Length = 0 then
        false
    else
        // InnoDB verifies multi-token phrases against lowercased document text,
        // but finds their starting positions through the original first token.
        let foldDocument = words.Length > 1 && isBinaryCollation collation
        seq { 0 .. doc.Length - words.Length }
        |> Seq.exists (fun start ->
            doc.[start].Key = words.[0].Key
            && (words
                |> Array.indexed
                |> Array.forall (fun (offset, word) ->
                    let token = doc.[start + offset]
                    let key = if foldDocument then collation.FullTextKeyOf(token.Text.ToLowerInvariant()) else token.Key
                    key = word.Key)))

let private phraseCandidates (view: ReadView<'id>) (words: Token[]) =
    words
    // InnoDB omits short terms and stopwords from postings but retains their
    // positions after the first searchable word in a phrase.
    |> Array.filter (isBooleanSearchable view.Rules)
    |> Array.distinctBy _.Key
    |> Array.map (fun word ->
        booleanPostingRows view word
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
    let tokenizer, collation = view.Rules.Tokenizer, view.Collation
    if usesUtf8WordParser tokenizer collation then
        // InnoDB folds query text before the lexer finds word boundaries.
        foldFullText collation query
        |> rawTokensWith tokenizer collation
        |> Array.map (tokenOfFolded collation)
    else
        tokensWith tokenizer collation query

let private naturalTerms (view: ReadView<'id>) (query: string) : string[] =
    let tokens = queryTokens view query
    let terms =
        match view.Rules.Tokenizer with
        | Words lengths -> tokens |> Array.filter (hasLookupWordLength lengths)
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
    match view.Rules.Tokenizer with
    | Ngrams _ -> queryTokens view query |> Array.map (fun word -> NaturalWord word.Key)
    | Words _ ->
        let words = queryTokens view
        let plain text = words text |> Array.filter (isExactLookup view.Rules) |> Array.map (fun word -> NaturalWord word.Key)
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
                |> Array.skipWhile (isSearchable view.Rules >> not)
                |> NaturalPhrase
                |> clauses.Add
                start <- closing + 1
        clauses.ToArray()

let private naturalClauseKeys rules = function
    | NaturalWord key -> [| key |]
    | NaturalPhrase words -> words |> Array.filter (isSearchable rules) |> Array.map _.Key

let private needsNaturalWordEvaluation (view: ReadView<'id>) (query: string) =
    let terms = queryTokens view query |> Array.filter (isExactLookup view.Rules)
    let repeated = terms.Length <> (terms |> Array.distinctBy _.Key).Length
    match view.Rules.Tokenizer with
    | Ngrams _ -> repeated
    | Words _ -> query.Contains '"' || repeated

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
            view.Documents.[id].Content.Fields |> Array.exists (fun field -> exactPhraseMatches view.Collation field words))

/// Matching terms retain their row sets separately from query occurrence counts.
/// Repeated query words increase MySQL's document frequency, not a row's TF.
let private naturalWordMatches candidateIds (view: ReadView<'id>) clauses =
    let addClause matches clause =
        let ids = naturalClauseDocuments view clause
        let ids = candidateIds |> Option.map (Set.intersect ids) |> Option.defaultValue ids
        if ids.IsEmpty then matches
        else
            naturalClauseKeys view.Rules clause
            |> Array.fold (fun matches key ->
                Map.change key (fun previous ->
                    Some(Set.union ids (Option.defaultValue Set.empty previous))) matches) matches

    { Occurrences = clauses |> Array.collect (naturalClauseKeys view.Rules) |> Array.countBy id |> Map.ofArray
      Documents = clauses |> Array.fold addClause Map.empty }

let private queryOccurrenceWeight (view: ReadView<'id>) documentFrequency occurrences =
    let frequency = float documentFrequency * float occurrences
    max (abs (log10 (float view.DocumentCount / frequency))) idfFloor

let private scoreNaturalWordMatches (view: ReadView<'id>) matches =
    matches.Documents
    |> Map.fold (fun scores key ids ->
        let rows = view.Postings.[key]
        let weight = queryOccurrenceWeight view rows.Count matches.Occurrences.[key]
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

let private booleanGroupSyntaxError (query: string) =
    let mutable depth = 0
    let mutable inPhrase = false
    let mutable error = None

    for c in query do
        if error.IsNone then
            if c = '"' then
                inPhrase <- not inPhrase
            elif not inPhrase then
                match c with
                | '(' -> depth <- depth + 1
                | ')' when depth = 0 -> error <- Some "syntax error, unexpected ')', expecting $end"
                | ')' -> depth <- depth - 1
                | _ -> ()

    if error.IsSome then error
    elif depth <> 0 then Some "syntax error, unexpected $end"
    else None

let internal booleanSyntaxErrorInView (view: ReadView<'id>) query =
    match view.Rules.Tokenizer with
    | Words _ -> booleanGroupSyntaxError query
    | Ngrams _ -> None

// These are the BMP word-boundary changes after MySQL's collation-specific
// lowercase conversion. The query keeps its spelling for token lookup.
let private isUnicode520BooleanNgramChar c =
    match int c with
    | 0x03CF | 0x1E9E | 0x2C6D | 0x2C6E | 0x2C6F | 0x2C70 | 0x2C7E | 0x2C7F | 0xA77D -> true
    | _ -> BooleanWordChars.contains c

let private isUnicode900BooleanNgramChar c =
    match int c with
    | code when code >= 0x13A0 && code <= 0x13F4 -> false // Cherokee lowercase letters are outside the historical word table.
    | 0x037F | 0xA78D | 0xA7AA | 0xA7AB | 0xA7AC | 0xA7AD | 0xA7AE | 0xA7B0 | 0xA7B1 | 0xA7B2 -> true
    | _ -> isUnicode520BooleanNgramChar c

let private parseBooleanQuery rules (collation: Collation) (query: string) : (BoolOp * BoolTerm) list =
    let policy, tokenizer = rules.Stopwords, rules.Tokenizer
    let utf8Words = usesUtf8WordParser tokenizer collation
    let query = if utf8Words then foldFullText collation query else query
    let isWord =
        match tokenizer with
        | Ngrams _ when not (isBinaryCollation collation) ->
            if collation.Name.Contains("_0900_", StringComparison.Ordinal) then isUnicode900BooleanNgramChar
            elif collation.Name.EndsWith("_unicode_520_ci", StringComparison.Ordinal) then isUnicode520BooleanNgramChar
            else BooleanWordChars.contains
        | _ -> BooleanWordChars.contains
    let mutable i = 0
    let len = query.Length
    let isNgram = match tokenizer with Ngrams _ -> true | Words _ -> false
    let mutable afterAsciiSpace = true
    let keepApostrophes =
        match tokenizer with
        | Words _ -> true
        | Ngrams _ -> false

    let rec skipSpace () =
        if i < len && Char.IsWhiteSpace query.[i] then
            i <- i + 1
            skipSpace ()

    let readWord () =
        let start = i
        while i < len && (isWord query.[i] || (keepApostrophes && query.[i] = '\'' && i > start)) do
            i <- i + 1
        let text = query.Substring(start, i - start).Trim('\'')
        tokenWith collation text, text

    let phraseWords phrase =
        let words =
            match tokenizer with
            | Words _ ->
                let token = if utf8Words then tokenOfFolded collation else tokenWith collation
                rawTokensBy isWord (not utf8Words) phrase |> Array.map token
            | Ngrams size ->
                rawTokensBy isWord false phrase
                |> Array.collect (fun word ->
                    if runeLength word < size then [| tokenWith collation word |]
                    else tokensWith tokenizer collation word)

        words
        |> Array.skipWhile (fun word ->
            match tokenizer with
            | Words _ -> not (isSearchable rules word)
            | Ngrams _ -> containsNgramStopword policy word.Text)

    let wordChunkBoundary = function
        | '"' | ' ' | '\n' | '*' | '(' | ')' | '+' | '-' | '<' | '>' | '~' | '@' | '%' -> true
        | _ -> false

    let readWordChunk (lengths: StorageOptions.WordLengths) =
        let start = i
        while i < len && not (wordChunkBoundary query.[i]) do
            i <- i + 1

        let words =
            rawTokensBy isWord false (query.Substring(start, i - start))
            |> Array.filter (fun word -> word.Length <= lengths.Maximum)
            |> Array.indexed
            |> Array.choose (fun (index, word) ->
                if index = 0 || word.Length >= lengths.Minimum then Some(tokenOfFolded collation word)
                else None)

        let prefix = i < len && query.[i] = '*'
        if prefix then i <- i + 1
        words |> Array.mapi (fun index word -> BWord(word, prefix && index = words.Length - 1))

    let readNgramOperator () =
        let mutable requirement = Optional
        let mutable weight = 0
        let mutable soft = false
        let mutable atTerm = false

        let delimiter c =
            afterAsciiSpace <- c = ' '
            requirement <- Optional
            weight <- 0
            soft <- false

        while i < len && not atTerm do
            match query.[i] with
            | '(' | ')' | '"' -> atTerm <- true
            | c when isWord c -> atTerm <- true
            | c ->
                // Punctuation after a word separates terms. Only an ASCII
                // space (or the beginning of the query) enables modifiers.
                if afterAsciiSpace then
                    match c with
                    | '+' -> requirement <- Must
                    | '-' -> requirement <- MustNot
                    | '>' -> weight <- weight + 1
                    | '<' -> weight <- weight - 1
                    | '~' -> soft <- not soft
                    | _ -> delimiter c
                else delimiter c
                i <- i + 1

        match requirement with
        | Must | MustNot -> requirement
        | _ when weight > 0 -> Raise
        | _ when weight < 0 -> Lower
        | _ when soft -> Soft
        | _ -> Optional

    // Cap parenthesis nesting so a query like "((((...))))" with thousands
    // of groups can't overflow the recursive-descent stack (a
    // StackOverflowException is not catchable and would kill the process).
    // Past the cap, an open paren is treated as ignorable punctuation.
    let maxDepth = 64

    let rec nodes (depth: int) : (BoolOp * BoolTerm) list =
        let acc = ResizeArray()
        let mutable go = true

        while go do
            let ngramOperator =
                if isNgram then readNgramOperator ()
                else
                    skipSpace ()
                    Optional

            if i >= len then go <- false
            elif query.[i] = ')' then
                i <- i + 1 // Ngrams reject stray closes before parsing; word queries retain their existing path.
                go <- false
            else
                let op =
                    if isNgram then ngramOperator
                    else
                        let operator =
                            match query.[i] with
                            | '+' -> i <- i + 1; Must
                            | '-' -> i <- i + 1; MustNot
                            | '>' -> i <- i + 1; Raise
                            | '<' -> i <- i + 1; Lower
                            | '~' -> i <- i + 1; Soft
                            | _ -> Optional
                        skipSpace ()
                        operator

                if i >= len then go <- false
                elif query.[i] = '(' then
                    i <- i + 1
                    if depth < maxDepth then acc.Add(op, BGroup(nodes (depth + 1)))
                elif query.[i] = '"' then
                    i <- i + 1
                    let start = i
                    while i < len && query.[i] <> '"' do
                        i <- i + 1
                    let phrase = query.Substring(start, i - start)
                    if isNgram && i > start then afterAsciiSpace <- query.[i - 1] = ' '
                    if i < len then i <- i + 1 // closing quote
                    let afterQuote = i
                    skipSpace ()
                    if isNgram && i > afterQuote then afterAsciiSpace <- query.[i - 1] = ' '

                    let proximity =
                        if i < len && query.[i] = '@' then
                            if isNgram then afterAsciiSpace <- false
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
                        | Words _ -> proximity
                        | Ngrams _ -> None

                    acc.Add(op, BPhrase(words, proximity))
                elif utf8Words && not (wordChunkBoundary query.[i]) then
                    match tokenizer with
                    | Words lengths ->
                        for term in readWordChunk lengths do
                            acc.Add(op, term)
                    | Ngrams _ -> ()
                elif isWord query.[i] then
                    let w, sourceWord = readWord ()
                    if isNgram then afterAsciiSpace <- false
                    let prefix = i < len && query.[i] = '*'
                    if prefix then i <- i + 1
                    if w.Text.Length > 0 then
                        let term =
                            match tokenizer with
                            | Words _ -> BWord(w, prefix)
                            | Ngrams size when runeLength w.Text < size -> BWord(w, prefix)
                            | Ngrams _ ->
                                let words = tokensWith tokenizer collation sourceWord |> Array.skipWhile (isSearchable rules >> not)
                                match words with
                                | [| word |] -> BWord(word, false)
                                | _ -> BPhrase(words, None)
                        acc.Add(op, term)
                else
                    // Punctuation MySQL's parser ignores.
                    i <- i + 1

        List.ofSeq acc

    match tokenizer with
    | Ngrams _ when booleanGroupSyntaxError query |> Option.isSome -> []
    | _ -> nodes 0

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

let private prefixWeight (view: ReadView<'id>) occurrences (posting: PrefixPosting<'id>) =
    match view.Rules.Tokenizer with
    | Ngrams _ when posting.GramDocumentCount > 0L ->
        let frequency = float posting.GramDocumentCount * float occurrences
        let documents = float view.DocumentCount
        if frequency = documents then idfFloor else log10 (documents / frequency)
    | _ -> idf view posting.Rows.Count

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
    | BWord(term, false) when not (isExactLookup view.Rules term) ->
        // Rejected words can still occupy positions inside a longer phrase.
        Map.empty
    | BWord(term, false) ->
        match booleanPostingRows view term with
        | None -> Map.empty
        | Some rows ->
            let weight = idf view rows.Count
            rows
            |> restrictScores candidateIds
            |> Map.map (fun _ frequency -> float frequency * weight * weight)
    | BWord(term, true) ->
        // Prefix wildcards bypass stopword and minimum-length rules.
        match Map.tryFind term.Key view.PrefixPostings with
        | None -> Map.empty
        | Some posting ->
            let weight = prefixWeight view 1 posting
            posting.Rows
            |> restrictScores candidateIds
            |> Map.map (fun _ frequency -> float frequency * weight * weight)
    | BPhrase(words, proximity) ->
        let candidates =
            phraseCandidates view words
            |> fun candidates -> candidateIds |> Option.map (Set.intersect candidates) |> Option.defaultValue candidates

        let terms =
            words
            |> Array.filter (isBooleanSearchable view.Rules)
            |> Array.distinctBy _.Key
            |> Array.choose (fun word ->
                booleanPostingRows view word
                |> Option.map (fun rows ->
                    let weight = idf view rows.Count
                    rows, weight * weight))

        candidates
        |> Seq.choose (fun id ->
            let fields = view.Documents.[id].Content.Fields
            let matches =
                match proximity with
                | None -> fields |> Array.exists (fun tokens -> exactPhraseMatches view.Collation tokens words)
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

let private tryRepeatedWordScores candidateIds (view: ReadView<'id>) nodes =
    let words =
        nodes |> List.choose (function
            | (Optional | Must as operator), BWord(word, false) -> Some(operator, word)
            | _ -> None)
    let isNgram =
        match view.Rules.Tokenizer with
        | Ngrams _ -> true
        | Words _ -> false
    if isNgram
       || words.Length <> nodes.Length
       || (words |> List.distinctBy (snd >> _.Key)).Length = words.Length then
        None
    else
        let candidates =
            (candidateIds, words)
            ||> List.fold (fun candidates (operator, word) ->
                if operator <> Must then candidates
                else
                    let ids =
                        if isExactLookup view.Rules word then
                            view.Postings |> Map.tryFind word.Key
                            |> Option.map (Map.keys >> Set.ofSeq) |> Option.defaultValue Set.empty
                        else Set.empty
                    intersectCandidates candidates (Some ids))
        words
        |> List.choose (fun (_, word) -> if isExactLookup view.Rules word then Some(NaturalWord word.Key) else None)
        |> List.toArray
        |> naturalWordMatches candidates view
        |> scoreNaturalWordMatches view
        |> Some

type private NgramScoringKey =
    | ExactNgram of string
    | NgramPrefix of string

type private NgramMatch =
    { Terms: Set<NgramScoringKey>
      Adjustment: int }

let private tryNgramRanking candidateIds (view: ReadView<'id>) nodes =
    match view.Rules.Tokenizer with
    | Words _ -> None
    | Ngrams _ ->
        let keys = function
            | BWord(word, true) -> [| NgramPrefix word.Key |]
            | BWord(word, false) when isExactLookup view.Rules word -> [| ExactNgram word.Key |]
            | BPhrase(words, _) ->
                words
                |> Array.filter (isBooleanSearchable view.Rules)
                |> Array.map (fun word -> ExactNgram word.Key)
            | _ -> [||]

        let rec leaves nodes =
            nodes
            |> Seq.collect (function
                | _, BGroup children -> leaves children
                | leaf -> Seq.singleton leaf)

        let rec hasRankAdjustments nodes =
            nodes |> List.exists (function
                | (Raise | Lower | Soft), _ -> true
                | _, BGroup children -> hasRankAdjustments children
                | _ -> false)

        let frequencies = leaves nodes |> Seq.collect (snd >> keys) |> Seq.countBy id |> Map.ofSeq

        let tracksPostingBounds =
            view.HasDocumentOrder
            && (nodes |> List.exists (function _, BGroup _ -> true | _ -> false))
            && (leaves nodes |> Seq.exists (function Must, BWord(_, true) -> true | _ -> false))

        let weighted = hasRankAdjustments nodes
        let repeated = frequencies |> Map.exists (fun _ count -> count > 1)

        if not tracksPostingBounds && not repeated && not weighted then
            None
        else
            // A wildcard exclusion visits postings only if its group has matches.
            // When its key recurs, that decision uses the full index result.
            let fullScope =
                leaves nodes
                |> Seq.exists (function
                    | MustNot, BWord(word, true) -> frequencies.[NgramPrefix word.Key] > 1
                    | _ -> false)

            // In a weighted query, a repeated gram can get its first position
            // from a document outside the SQL candidates.
            let needsCorpusOrder = weighted && repeated && frequencies.Count > 1
            let scope =
                if tracksPostingBounds || needsCorpusOrder then view.VisibleDocumentIds
                elif fullScope then None
                else candidateIds
            let visits = Collections.Generic.Dictionary<NgramScoringKey, int>()
            let contributionOrder = Collections.Generic.Dictionary<NgramScoringKey, int>()
            let mutable postingBounds = None

            let documentBounds rows =
                let order id = view.Documents.[id].Order
                if view.DocumentOrderFollowsIds then
                    order (fst (Map.minKeyValue rows)), order (fst (Map.maxKeyValue rows))
                else
                    rows |> Map.fold (fun (lower, upper) id _ ->
                        let position = order id
                        min lower position, max upper position) (Int64.MaxValue, 0L)

            let outsideBounds lower upper = function
                | ExactNgram key ->
                    view.Postings
                    |> Map.tryFind key
                    |> Option.forall (fun rows ->
                        let first, last = documentBounds rows
                        first > upper || last < lower)
                | NgramPrefix _ -> false

            let adjustmentSteps term id =
                match term with
                | BWord(word, true) ->
                    indexedTokens view.Documents.[id].Content
                    |> Seq.filter (prefixKeys view.Collation >> Array.contains word.Key)
                    |> Seq.distinctBy _.Key
                    |> Seq.truncate 2
                    |> Seq.length
                | _ -> 1

            let adjust term id delta current =
                // The second gram can move a rank from the opposite bound past zero.
                let steps = if current = -delta then adjustmentSteps term id else 1
                Math.Clamp(current + steps * delta, -1, 1)

            let rec matchTerm isRequired contributes = function
                | BGroup children -> matchNodes children
                | term ->
                    let found = keys term
                    let firstOutside =
                        match isRequired, postingBounds with
                        | true, Some(lower, upper) -> Array.tryFindIndex (outsideBounds lower upper) found
                        | _ -> None
                    // InnoDB counts the skipped gram's frequency before testing
                    // its posting bounds, then stops at that gram.
                    let visited = firstOutside |> Option.map (fun index -> Array.take (index + 1) found) |> Option.defaultValue found
                    for key in visited do
                        match visits.TryGetValue key with
                        | true, count -> visits.[key] <- count + 1
                        | false, _ -> visits.Add(key, 1)

                    if firstOutside.IsSome then Map.empty
                    else
                        let matches = evalTerm scope view term
                        match contributes with
                        | Some accepts when
                            (found |> Array.exists (contributionOrder.ContainsKey >> not))
                            && (matches |> Map.exists (fun id _ -> accepts id)) ->
                            // A gram gets its position when it first contributes to a document.
                            for key in found do
                                contributionOrder.TryAdd(key, contributionOrder.Count) |> ignore
                        | _ -> ()
                        let terms = Set.ofArray found
                        matches |> Map.map (fun _ _ -> { Terms = terms; Adjustment = 0 })

            and matchNodes nodes =
                // Ordinary terms seed the group before required terms and exclusions.
                let optional, filters = nodes |> List.partition (fun (op, _) -> op <> Must && op <> MustNot)
                let required, excluded = filters |> List.partition (fun (op, _) -> op = Must)

                let union matches (operator, term) =
                    let contributes id = operator <> Soft || Map.containsKey id matches
                    matchTerm false (Some contributes) term
                    |> Map.fold (fun matches id incoming ->
                        matches
                        |> Map.change id (fun existing ->
                            match operator, existing with
                            | Soft, None -> None
                            | _ ->
                                let current = existing |> Option.defaultValue incoming
                                let adjustment =
                                    match operator with
                                    | Raise -> adjust term id 1 current.Adjustment
                                    | Lower | Soft -> adjust term id -1 current.Adjustment
                                    | _ -> current.Adjustment

                                Some { Terms = Set.union current.Terms incoming.Terms
                                       Adjustment = adjustment })) matches

                let intersect first matches (_, term) =
                    let contributes id = first || Map.containsKey id matches
                    let requiredMatches =
                        match term with
                        | BWord(_, true) when tracksPostingBounds ->
                            if not first && Map.isEmpty matches then Map.empty
                            else
                                // Required prefixes set query-wide posting bounds.
                                // Leaving a group preserves those bounds for later phrases.
                                postingBounds <- if first then None else Some(documentBounds matches)
                                matchTerm true (Some contributes) term
                        | _ -> matchTerm true (Some contributes) term

                    requiredMatches
                    |> Map.fold (fun narrowed id incoming ->
                        match Map.tryFind id matches with
                        | Some current ->
                            let combined =
                                { Terms = Set.union current.Terms incoming.Terms
                                  Adjustment = Math.Clamp(current.Adjustment + incoming.Adjustment, -1, 1) }
                            Map.add id combined narrowed
                        | None when first -> Map.add id incoming narrowed
                        | None -> narrowed) Map.empty

                let included = List.fold union Map.empty optional
                let filtered =
                    match required with
                    | first :: rest -> List.fold (intersect false) (intersect true included first) rest
                    | [] -> included

                (filtered, excluded)
                ||> List.fold (fun matches (_, term) ->
                    match term with
                    | BWord(_, true) when matches.IsEmpty -> matches
                    | _ -> matchTerm false None term |> Map.fold (fun matches id _ -> Map.remove id matches) matches)

            let scoreTerm id term =
                let count = visits.[term]
                let rows, weight =
                    match term with
                    | NgramPrefix key ->
                        let posting = view.PrefixPostings.[key]
                        posting.Rows, prefixWeight view count posting
                    | ExactNgram key ->
                        let rows = view.Postings.[key]
                        rows, queryOccurrenceWeight view rows.Count count

                float rows.[id] * weight * weight

            matchNodes nodes
            |> (if fullScope || tracksPostingBounds || needsCorpusOrder then restrictScores candidateIds else id)
            |> Map.map (fun id matched ->
                matched.Terms
                |> Seq.sortBy (fun term -> contributionOrder.[term])
                |> Seq.fold (fun rank term -> rank + float32 (scoreTerm id term)) (float32 matched.Adjustment)
                |> float)
            // A rank modifier can cancel the final relevance. MATCH then returns zero.
            |> Map.filter (fun _ score -> score <> 0.0)
            |> Some

let internal booleanScoresInView candidateIds (view: ReadView<'id>) (query: string) =
    let candidateIds = candidatesInView candidateIds view
    let nodes = parseBooleanQuery view.Rules view.Collation query
    if (nodes |> List.forall (fun (operator, _) -> operator = MustNot))
       || (candidateIds |> Option.exists Set.isEmpty) then
        Map.empty
    else
        tryNgramRanking candidateIds view nodes
        |> Option.orElseWith (fun () -> tryRepeatedWordScores candidateIds view nodes)
        |> Option.defaultWith (fun () -> evalNodes candidateIds view nodes)
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

    let scoreTerms terms =
        let postingFor (term: Token, prefix) =
            if prefix then
                Map.tryFind term.Key view.PrefixPostings
                |> Option.map (fun posting -> posting.Rows, prefixWeight view 1 posting)
            elif isExactLookup view.Rules term then
                booleanPostingRows view term |> Option.map (fun rows -> rows, idf view rows.Count)
            else None

        let postings =
            terms
            |> List.map (fun (op, term, prefix) ->
                let rows, weight = postingFor (term, prefix) |> Option.defaultValue (Map.empty, 0.0)
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
                    if posting.Rows.Count <= totals.Count then
                        for KeyValue(id, _) in posting.Rows do
                            totals.Remove id |> ignore
                    else
                        for id in totals.Keys |> Seq.toArray do
                            if Map.containsKey id posting.Rows then
                                totals.Remove id |> ignore

            let scores = Collections.Generic.Dictionary<'id, float>(totals.Count)

            totals.Keys
            |> Seq.sort
            |> Seq.iter (fun id ->
                let score = totals.[id]
                scores.Add(id, visibleBooleanScore score))

            scores

        let postingWork =
            postings
            |> List.sumBy (fun posting -> int64 posting.Rows.Count)

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

            match candidateIds, candidateProbeWork with
            | _, _ when positiveTerms |> List.forall (fun posting -> posting.Rows.IsEmpty) ->
                Collections.Generic.Dictionary<'id, float>()
            | Some candidates, Some work when work < postingWork -> scoreCandidates candidates candidates.Count
            | _ -> accumulatePositivePostings ()

    let nodes = parseBooleanQuery view.Rules view.Collation query
    let flat = flatTerms [] nodes
    let onlyExclusions = nodes |> List.forall (fun (operator, _) -> operator = MustNot)

    if onlyExclusions || (candidateIds |> Option.exists Set.isEmpty) then
        flat |> Option.map (fun _ -> Collections.Generic.Dictionary<'id, float>())
    else
        match tryNgramRanking candidateIds view nodes
              |> Option.orElseWith (fun () -> tryRepeatedWordScores candidateIds view nodes) with
        | Some scores ->
            let result = Collections.Generic.Dictionary<'id, float>()
            scores |> Map.iter (fun id score -> result.Add(id, visibleBooleanScore score))
            Some result
        | None -> flat |> Option.map scoreTerms

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
// Query expansion uses searchable tokens from every first-pass match.
// InnoDB does not apply the MyISAM ft_query_expansion_limit.
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
        |> Array.collect (fun (id, _) ->
            let document = view.Documents.[id].Content
            // Seed words are extracted again using the current length settings.
            let rules =
                match view.Rules.Tokenizer with
                | Words _ -> { document.Rules with Tokenizer = view.Rules.Tokenizer }
                | Ngrams _ -> document.Rules
            indexedTokens { document with Rules = rules })
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
