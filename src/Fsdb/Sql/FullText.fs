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

let private collectRawTokens isWord keepApostrophes makeToken (text: string) =
    let tokens = ResizeArray<_>()
    let current = Text.StringBuilder()
    let mutable start = 0

    let flush () =
        let text = current.ToString()
        let t = if keepApostrophes then text.Trim('\'') else text
        if t.Length > 0 then tokens.Add(makeToken t start)
        current.Clear() |> ignore

    for offset = 0 to text.Length - 1 do
        let c = text.[offset]
        if isWord c then
            if current.Length = 0 then start <- offset
            current.Append c |> ignore
        elif keepApostrophes && c = '\'' && current.Length > 0 then current.Append c |> ignore
        else flush ()

    flush ()
    tokens.ToArray()

let private rawTokensBy isWord keepApostrophes =
    collectRawTokens isWord keepApostrophes (fun word _ -> word)

let private rawTokens = rawTokensBy isWordChar true

let private positionedNgrams size (text: string) =
    let boundary (rune: Text.Rune) =
        rune.IsAscii && not (Text.Rune.IsLetterOrDigit rune || rune.Value = int '_')

    let mutable offset = 0L
    [| for rune in text.EnumerateRunes() do
           yield struct (rune, offset)
           offset <- offset + int64 rune.Utf8SequenceLength |]
    |> Array.windowed size
    |> Array.choose (fun runes ->
        if runes |> Array.exists (fun struct (rune, _) -> boundary rune) then None
        else
            let struct (_, first) = runes.[0]
            let gram = runes |> Array.map (fun struct (rune, _) -> string rune) |> String.concat ""
            Some(gram, first))

let private ngrams size text = positionedNgrams size text |> Array.map fst

let tokenize (text: string) : string[] = rawTokens text |> Array.map _.ToLowerInvariant()

// ---------------------------------------------------------------------------
// Corpus compatibility wrapper over the immutable index.
// ---------------------------------------------------------------------------

type private Token =
    { Text: string
      Key: string
      ByteOffset: int64 }

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

type private DeletedPostings =
    { Documents: Map<int64, string[]>
      Words: Map<string, Map<int64, int64>>
      Prefixes: Map<string, Map<int64, int64>> }

let private emptyDeletedPostings =
    { Documents = Map.empty; Words = Map.empty; Prefixes = Map.empty }

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
          FlushedThrough: int64 option
          CachedTerms: FullTextCache.Terms
          FlushedAccentTerms: FullTextCache.Terms
          CacheBudget: FullTextCache.Budget
          CacheCycle: obj
          Deleted: DeletedPostings
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
          FlushedThrough: int64 option
          CachedTerms: FullTextCache.Terms
          FlushedAccentTerms: FullTextCache.Terms
          CachedPostingTails: Map<string, FullTextCache.PostingTail>
          Deleted: DeletedPostings
          DocumentOrderFollowsIds: bool
          HasDocumentOrder: bool }

let private tracksNgramPostingSources (index: Index<'id>) =
    match index.Rules.Tokenizer with
    | Ngrams _ -> true
    | Words _ -> false

let internal readView (index: Index<'id>) : ReadView<'id> =
    { Documents = index.Documents
      Postings = index.Postings
      PrefixPostings = index.PrefixPostings
      Collation = index.Collation
      Rules = activeRules index
      DocumentCount = index.Documents.Count
      VisibleDocumentIds = None
      FlushedThrough =
          if tracksNgramPostingSources index then
              index.FlushedThrough |> Option.filter (fun through -> through < index.NextDocumentOrder - 1L)
          else None
      CachedTerms = index.CachedTerms
      FlushedAccentTerms = index.FlushedAccentTerms
      CachedPostingTails = index.CacheBudget.Tails
      Deleted = index.Deleted
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

let private isUtf8Collation (collation: Collation) =
    collation.Name.StartsWith("utf8", StringComparison.Ordinal)

let private fullTextQueryText collation (query: string) =
    // InnoDB's in-place UTF-8 lowercase conversion stops at NUL.
    // The legacy Turkish mappings can expand and use a length-aware conversion.
    let terminator = query.IndexOf('\u0000')
    if terminator >= 0
       && isUtf8Collation collation
       && not (isBinaryCollation collation)
       && not (collation.Name.EndsWith("_turkish_ci", StringComparison.Ordinal)) then
        query.Substring(0, terminator)
    else
        query

let private usesUtf8WordParser tokenizer (collation: Collation) =
    match tokenizer with
    | Words _ -> isUtf8Collation collation
    | Ngrams _ -> false

let private foldFullText (collation: Collation) text =
    if isBinaryCollation collation then text
    else Collation.lowercaseForFullText collation.Name text

let private tokenOfFolded (collation: Collation) text =
    { Text = text
      Key = collation.FullTextKeyOf text
      ByteOffset = 0L }

let private tokenWith collation text =
    // InnoDB folds full-text case even for nonbinary case-sensitive collations.
    foldFullText collation text |> tokenOfFolded collation

let private collectWordTokens tokenizer collation makeToken =
    if usesUtf8WordParser tokenizer collation then collectRawTokens BooleanWordChars.contains false makeToken
    else collectRawTokens isWordChar true makeToken

let private rawTokensWith tokenizer collation text =
    match tokenizer with
    | Words _ -> collectWordTokens tokenizer collation (fun word _ -> word) text
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

let private cacheDocumentTerms (words: (string * Token[])[]) terms =
    (terms, Array.sortBy fst words)
    ||> Array.fold (fun terms (key, grams) -> FullTextCache.add key grams.[0].Text terms)

let private usesAccentSensitivePrefixes (collation: Collation) = function
    | Ngrams _ -> collation.Name = "utf8mb4_0900_as_ci"
    | Words _ -> false

let private addFlushedAccentTerm (collation: Collation) terms spelling =
    let key = Uca0900Weights.accentSensitiveKey spelling
    FullTextCache.add key (collation.FullTextKeyOf spelling) terms

let private rebuildFlushedAccentTerms (index: Index<'id>) =
    if not (usesAccentSensitivePrefixes index.Collation index.Rules.Tokenizer) then index
    elif index.FlushedThrough = Some 0L then { index with FlushedAccentTerms = FullTextCache.empty }
    else
        let through = defaultArg index.FlushedThrough Int64.MaxValue
        let spellings =
            seq {
                for document in Map.values index.Documents do
                    if document.Order <= through then
                        for token in indexedTokens document.Content do
                            yield token.Text
                for KeyValue(order, words) in index.Deleted.Documents do
                    if order <= through then yield! words
            }
        { index with
            FlushedAccentTerms =
                spellings
                |> Seq.distinct
                |> Seq.fold (addFlushedAccentTerm index.Collation) FullTextCache.empty }

let private postingByteCount tokenizer (collation: Collation) =
    let utf8 (text: string) start length = Text.Encoding.UTF8.GetByteCount(text.AsSpan(start, length))
    match tokenizer with
    | Ngrams _ -> utf8
    | Words _ when usesUtf8WordParser tokenizer collation -> utf8
    | Words _ ->
        let charset = Collation.charsetOfCollation collation.Name
        fun text start length -> (Charset.encode charset (text.Substring(start, length))).Length

let private chargeDocument tokenizer collation order (words: (string * Token[])[]) budget =
    let byteCount = postingByteCount tokenizer collation
    let postings =
        words |> Array.map (fun (key, grams) ->
            let mutable previous = 0L
            let mutable bytes = 0L
            for gram in grams do
                bytes <- bytes + FullTextCache.encodedLength (gram.ByteOffset - previous)
                previous <- gram.ByteOffset
            let spelling = grams.[0].Text
            key, byteCount spelling 0 spelling.Length, bytes)
    FullTextCache.addDocument order postings budget

let private prefixKeysFor forCache (collation: Collation) (token: Token) =
    let mutable length = 0
    [| for rune in token.Text.EnumerateRunes() do
           let start = length
           length <- length + rune.Utf16SequenceLength
           yield collation.FullTextKeyOf(token.Text.Substring(0, length))
           if rune.Value > 0x7F then
               match Collation.fullTextPrefixExpansion forCache collation.Name rune with
               | Some expansion ->
                   let leading = token.Text.Substring(0, start)
                   let mutable offset = 0
                   for part in expansion.EnumerateRunes() do
                       offset <- offset + part.Utf16SequenceLength
                       if offset < expansion.Length then
                           yield collation.FullTextKeyOf(leading + expansion.Substring(0, offset))
               | None -> () |]
    |> Array.distinct

let private prefixKeys collation token = prefixKeysFor false collation token

let private ngramDocumentPrefixes collation (words: (string * Token[])[]) =
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

let private documentPrefixes collation tokenizer tokens words =
    match tokenizer with
    | Words _ ->
        tokens
        |> Array.collect (prefixKeys collation)
        |> Array.countBy id
        |> Array.map (fun (key, frequency) -> key, (frequency, 0L))
    | Ngrams _ -> ngramDocumentPrefixes collation words

let private removePosting id key postings =
    match Map.tryFind key postings with
    | None -> postings
    | Some rows ->
        let remaining = Map.remove id rows
        if remaining.IsEmpty then Map.remove key postings else Map.add key remaining postings

let private addDeletedDocument order (tokens: Token[]) words prefixes (history: DeletedPostings) =
    if Array.isEmpty tokens then history
    else
        let add rows (key, frequency) =
            let positions = Map.tryFind key rows |> Option.defaultValue Map.empty
            Map.add key (Map.add order frequency positions) rows
        { Documents = Map.add order (tokens |> Array.map _.Text |> Array.distinct) history.Documents
          Words = words |> Array.fold (fun rows (key, _) -> add rows (key, 1L)) history.Words
          Prefixes = prefixes |> Array.fold (fun rows (key, (_, count)) -> add rows (key, count)) history.Prefixes }

let private deletedTokens (collation: Collation) texts =
    texts |> Array.map (tokenOfFolded collation)

let private removeDeletedDocument collation order texts (history: DeletedPostings) =
    let tokens = deletedTokens collation texts
    let words = tokens |> Array.groupBy _.Key
    let prefixes = ngramDocumentPrefixes collation words
    { Documents = Map.remove order history.Documents
      Words = words |> Array.fold (fun rows (key, _) -> removePosting order key rows) history.Words
      Prefixes = prefixes |> Array.fold (fun rows (key, _) -> removePosting order key rows) history.Prefixes }

let private emptyIndexWith policy tokenizer (collation: Collation) : Index<'id> =
    { Documents = Map.empty
      Postings = Map.empty
      PrefixPostings = Map.empty
      Collation = collation
      Rules = { Tokenizer = tokenizer; Stopwords = policy }
      StopwordSource = None
      StopwordReload = None
      NextDocumentOrder = 1L
      FlushedThrough =
          match tokenizer with
          | Ngrams _ when collation.Name = "utf8mb4_0900_as_ci" -> Some 0L
          | _ -> None
      CachedTerms = FullTextCache.empty
      FlushedAccentTerms = FullTextCache.empty
      CacheBudget = FullTextCache.emptyBudget
      CacheCycle = obj ()
      Deleted = emptyDeletedPostings
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

let private removeDocumentCore retainPosting (id: 'id) (index: Index<'id>) : Index<'id> =
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
            PrefixPostings = prefixPostings
            Deleted =
                if retainPosting && index.FlushedThrough.IsSome && tracksNgramPostingSources index then
                    addDeletedDocument indexed.Order tokens words prefixes index.Deleted
                else index.Deleted }

let removeDocument id index = removeDocumentCore true id index

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
        CachedTerms =
            if index.FlushedThrough.IsSome then cacheDocumentTerms words index.CachedTerms
            else index.CachedTerms
        CacheBudget =
            if index.FlushedThrough.IsSome then
                chargeDocument document.Rules.Tokenizer index.Collation index.NextDocumentOrder words index.CacheBudget
            else index.CacheBudget
        HasDocumentOrder = index.HasDocumentOrder || index.Documents.IsEmpty
        DocumentOrderFollowsIds =
            index.Documents.IsEmpty
            || (index.DocumentOrderFollowsIds && id > fst (Map.maxKeyValue index.Documents)) }

let internal addDocumentFieldsWithRules rules id texts (index: Index<'id>) =
    // Phrase positions retain tokens omitted from ngram postings.
    let byteCount = postingByteCount rules.Tokenizer index.Collation
    let tokensAt start (text: string) =
        match rules.Tokenizer with
        | Ngrams size ->
            positionedNgrams size text |> Array.map (fun (gram, offset) ->
                { tokenWith index.Collation gram with ByteOffset = start + offset })
        | Words _ ->
            let mutable previous = 0
            let mutable position = start
            collectWordTokens rules.Tokenizer index.Collation (fun word offset ->
                position <- position + int64 (byteCount text previous (offset - previous))
                previous <- offset
                { tokenWith index.Collation word with ByteOffset = position }) text

    let mutable start = 0L
    let fields =
        [| for text in texts do
               yield tokensAt start text
               if text.Length > 0 then start <- start + int64 (byteCount text 0 text.Length) + 1L |]
    addDocumentData id { Fields = fields; Rules = rules } index

let internal sameDocument id (left: Index<'id>) (right: Index<'id>) =
    match Map.tryFind id left.Documents, Map.tryFind id right.Documents with
    | Some left, Some right -> obj.ReferenceEquals(left.Content, right.Content)
    | _ -> false

let internal mergeDocuments sourceRowIds (baseline: Index<'id>) (source: Index<'id>) (live: Index<'id>) (target: Index<'id>) =
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
        |> List.fold (fun index (id, document) ->
            // Row publication may already have installed a temporary replacement.
            let index = if sameDocument id live index then index else removeDocumentCore false id index
            addDocumentData id document.Content index) target

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
        FlushedThrough = source.FlushedThrough
        CachedTerms = source.CachedTerms
        FlushedAccentTerms = source.FlushedAccentTerms
        CacheBudget = source.CacheBudget
        CacheCycle = source.CacheCycle
        Deleted = source.Deleted
        DocumentOrderFollowsIds = source.DocumentOrderFollowsIds
        HasDocumentOrder = source.HasDocumentOrder }

let internal syncCachedPostings (index: Index<'id>) =
    let through = Some(index.NextDocumentOrder - 1L)
    if index.FlushedThrough = through then index
    else
        { index with
            FlushedThrough = through
            CachedTerms = FullTextCache.empty
            FlushedAccentTerms =
                if usesAccentSensitivePrefixes index.Collation index.Rules.Tokenizer then
                    FullTextCache.inInsertionOrder index.CachedTerms
                    |> List.fold (addFlushedAccentTerm index.Collation) index.FlushedAccentTerms
                else index.FlushedAccentTerms
            CacheBudget = FullTextCache.emptyBudget
            CacheCycle = obj () }

let internal onlyCacheSynced (before: Index<'id>) (after: Index<'id>) =
    obj.ReferenceEquals(before, after)
    || (before.FlushedThrough <> after.FlushedThrough
        && after.FlushedThrough = Some(after.NextDocumentOrder - 1L)
        && before.NextDocumentOrder = after.NextDocumentOrder
        && obj.ReferenceEquals(before.Documents, after.Documents)
        && obj.ReferenceEquals(before.Deleted, after.Deleted)
        && obj.ReferenceEquals(before.Postings, after.Postings)
        && obj.ReferenceEquals(before.PrefixPostings, after.PrefixPostings)
        && before.Rules = after.Rules && before.StopwordSource = after.StopwordSource
        && obj.ReferenceEquals(before.StopwordReload, after.StopwordReload))

let internal flushPostings index =
    let flushed = syncCachedPostings index
    if flushed.Deleted.Documents.IsEmpty then flushed
    else { flushed with Deleted = emptyDeletedPostings } |> rebuildFlushedAccentTerms

let internal flushedThrough (index: Index<'id>) = index.FlushedThrough

let internal nextDocumentOrder (index: Index<'id>) = index.NextDocumentOrder

let internal cachedTermsInInsertionOrder (index: Index<'id>) =
    FullTextCache.inInsertionOrder index.CachedTerms

let internal cacheBudget (index: Index<'id>) = index.CacheBudget

let internal cacheCycle (index: Index<'id>) = index.CacheCycle

let internal sameCacheCycle (before: Index<'id>) (after: Index<'id>) =
    obj.ReferenceEquals(before.CacheCycle, after.CacheCycle)

let internal addedCachedDocuments (before: Index<'id>) (after: Index<'id>) =
    after.FlushedThrough.IsSome
    && after.NextDocumentOrder > before.NextDocumentOrder
    && (after.CacheBudget.Bytes > before.CacheBudget.Bytes
        // An indexed write can request sync even when its text yields no grams.
        || (after.Documents |> Map.exists (fun _ document -> document.Order >= before.NextDocumentOrder)))

let internal cachedPostingTail spelling (index: Index<'id>) =
    Map.tryFind (index.Collation.FullTextKeyOf spelling) index.CacheBudget.Tails

let internal deletedPostings (index: Index<'id>) = index.Deleted.Documents

let internal restoreDeletedPostings (saved: Map<int64, string[]>) (index: Index<'id>) =
    if saved.IsEmpty then index
    else
        if index.Documents |> Map.exists (fun _ document -> saved.ContainsKey document.Order) then
            invalidOp "Persistence: repeated full-text document order"
        let history =
            saved
            |> Map.fold (fun history order texts ->
                let tokens = deletedTokens index.Collation texts
                let words = tokens |> Array.groupBy _.Key
                let prefixes = ngramDocumentPrefixes index.Collation words
                addDeletedDocument order tokens words prefixes history) emptyDeletedPostings
        { index with
            Deleted = history
            NextDocumentOrder = max index.NextDocumentOrder (1L + fst (Map.maxKeyValue saved)) }

let private cacheDocumentsAfter through (index: Index<'id>) initial =
    index.Documents
    |> Map.values
    |> Seq.filter (fun document -> document.Order > through)
    |> Seq.sortBy _.Order
    |> Seq.fold (fun (terms, budget) document ->
        let words = indexedTokens document.Content |> Array.groupBy _.Key
        cacheDocumentTerms words terms, chargeDocument document.Content.Rules.Tokenizer index.Collation document.Order words budget) initial

let internal restoreCache nextOrder through saved savedBudget hasSegmentEnds (index: Index<'id>) =
    let next = nextOrder |> Option.defaultValue index.NextDocumentOrder
    if next < index.NextDocumentOrder then
        invalidOp "Persistence: full-text high-water mark precedes a document"
    let flushed =
        match through with
        | Some through -> Some(min through (next - 1L))
        | None when usesAccentSensitivePrefixes index.Collation index.Rules.Tokenizer -> Some(next - 1L)
        | None when tracksNgramPostingSources index -> None
        | None -> Some(next - 1L)
    let index = { index with FlushedThrough = flushed; NextDocumentOrder = next } |> rebuildFlushedAccentTerms
    match index.FlushedThrough with
    | None -> index
    | Some through ->
        let rebuilt = lazy (cacheDocumentsAfter through index (FullTextCache.empty, FullTextCache.emptyBudget))
        let terms =
            match saved with
            | Some spellings ->
                spellings |> Seq.fold (fun terms text -> FullTextCache.add (index.Collation.FullTextKeyOf text) text terms) FullTextCache.empty
            | None ->
                // Older snapshots can reproduce the insertion order of live documents.
                fst rebuilt.Value
        let budget: FullTextCache.Budget =
            match savedBudget with
            | None -> snd rebuilt.Value
            | Some (bytes, tails) ->
                // Older checkpoints can recover segment boundaries if every charged posting is still live.
                let rebuiltTails =
                    lazy (
                        if hasSegmentEnds || not (tracksNgramPostingSources index) then Map.empty
                        else (snd rebuilt.Value).Tails)
                let tails = tails |> List.map (fun (spelling, tail: FullTextCache.PostingTail) ->
                    if tail.LastDocument <= through || tail.LastDocument >= index.NextDocumentOrder
                       || (tail.EarlierSegmentEnds |> List.exists (fun order -> order <= through)) then
                        invalidOp "Persistence: full-text posting tail outside the cache"
                    let key = index.Collation.FullTextKeyOf spelling
                    let restored =
                        match Map.tryFind key rebuiltTails.Value with
                        | Some rebuilt when rebuilt.LastDocument = tail.LastDocument && rebuilt.UsedBytes = tail.UsedBytes ->
                            let hasCachedDeletes =
                                index.Deleted.Words |> Map.tryFind key
                                |> Option.exists (fun orders -> fst (Map.maxKeyValue orders) > through)
                            if hasCachedDeletes then tail else { tail with EarlierSegmentEnds = rebuilt.EarlierSegmentEnds }
                        | _ -> tail
                    key, restored) |> Map.ofList
                { Bytes = bytes; Tails = tails }
        { index with CachedTerms = terms; CacheBudget = budget }

let internal publishCachedTerms (baseline: Index<'id>) (index: Index<'id>) =
    if index.FlushedThrough.IsNone
       || index.FlushedThrough <> baseline.FlushedThrough
       || index.NextDocumentOrder < baseline.NextDocumentOrder
       || (obj.ReferenceEquals(index.CachedTerms, baseline.CachedTerms)
           && obj.ReferenceEquals(index.CacheBudget, baseline.CacheBudget)
           && obj.ReferenceEquals(index.Deleted, baseline.Deleted)) then
        index
    else
        // Only final documents are indexed by a committing transaction.
        let terms, budget =
            if obj.ReferenceEquals(index.CacheBudget, baseline.CacheBudget) then index.CachedTerms, index.CacheBudget
            else cacheDocumentsAfter (baseline.NextDocumentOrder - 1L) index (baseline.CachedTerms, baseline.CacheBudget)
        let deleted =
            if index.Deleted.Documents.IsEmpty || fst (Map.maxKeyValue index.Deleted.Documents) < baseline.NextDocumentOrder then index.Deleted
            else
                index.Deleted.Documents |> Map.fold (fun history order texts ->
                    if order < baseline.NextDocumentOrder then history
                    else removeDeletedDocument index.Collation order texts history) index.Deleted
        { index with CachedTerms = terms; CacheBudget = budget; Deleted = deleted }

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

let private wordFrequencyWeight (view: ReadView<'id>) frequency =
    max (abs (log10 (float view.DocumentCount / frequency))) idfFloor

let private indexedDocumentCount (view: ReadView<'id>) term liveCount =
    match Map.tryFind term view.Deleted.Words with
    | Some deleted -> int64 liveCount + int64 deleted.Count
    | None -> int64 liveCount

let private indexedWordWeight view term liveCount =
    if view.Deleted.Words.IsEmpty then idf view liveCount
    else wordFrequencyWeight view (float (indexedDocumentCount view term liveCount))

let private termScoresWithin weightFor (candidateIds: Set<'id> option) (view: ReadView<'id>) (term: string) : Map<'id, float> =
    match Map.tryFind term view.Postings with
    | None -> Map.empty
    | Some rows ->
        let weight = weightFor term rows.Count
        let scale = weight * weight

        match candidateIds with
        | None -> rows |> Map.map (fun _ frequency -> float frequency * scale)
        | Some candidates ->
            candidates
            |> Seq.choose (fun id -> Map.tryFind id rows |> Option.map (fun frequency -> id, float frequency * scale))
            |> Map.ofSeq

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
let private sumTermScoresWithin weightFor candidateIds (view: ReadView<'id>) (terms: string[]) : Map<'id, float> =
    let scoreTerm = termScoresWithin weightFor candidateIds view
    match terms with
    | [||] -> Map.empty
    | [| term |] -> scoreTerm term
    | _ ->
        terms
        |> Array.skip 1
        |> Array.fold
            (fun scores term ->
                scoreTerm term
                |> Map.fold
                    (fun scores id score ->
                        Map.change id (fun current -> Some(score + Option.defaultValue 0.0 current)) scores)
                    scores)
            (scoreTerm terms.[0])

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

let private scoreNaturalWordMatches (view: ReadView<'id>) matches =
    matches.Documents
    |> Map.fold (fun scores key ids ->
        let rows = view.Postings.[key]
        let frequency = float (indexedDocumentCount view key rows.Count) * float matches.Occurrences.[key]
        let weight = wordFrequencyWeight view frequency
        ids |> Set.fold (fun scores id ->
            let contribution = float rows.[id] * weight * weight
            Map.change id (fun current -> Some(contribution + Option.defaultValue 0.0 current)) scores) scores) Map.empty

let private naturalScoresForQueryText candidateIds (view: ReadView<'id>) query =
    let candidateIds = candidatesInView candidateIds view
    if needsNaturalWordEvaluation view query then
        naturalWordClauses view query
        |> naturalWordMatches candidateIds view
        |> scoreNaturalWordMatches view
    else
        let terms = naturalTerms view query
        // InnoDB removes deleted postings from the single-term natural rank.
        let weightFor =
            if terms.Length = 1 then fun _ -> idf view
            else indexedWordWeight view
        sumTermScoresWithin weightFor candidateIds view terms

let internal naturalScoresInView candidateIds (view: ReadView<'id>) query =
    naturalScoresForQueryText candidateIds view (fullTextQueryText view.Collation query)

let naturalScores (index: Index<'id>) (query: string) : Map<'id, float> =
    naturalScoresInView None (readView index) query

let internal naturalScoresWithin (candidateIds: Set<'id>) (index: Index<'id>) (query: string) : Map<'id, float> =
    naturalScoresInView (Some candidateIds) (readView index) query

let internal tryNaturalSingleTermScoresDictionaryInView
    (candidateIds: Set<'id> option)
    (view: ReadView<'id>)
    (query: string)
    : Collections.Generic.Dictionary<'id, float> option =
    let query = fullTextQueryText view.Collation query
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
    | Words _ -> booleanGroupSyntaxError (fullTextQueryText view.Collation query)
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
    let query = fullTextQueryText collation query
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

                    let proximity =
                        if isNgram then
                            // The ngram lexer treats @ as punctuation. Digits
                            // after it belong to the next search term.
                            None
                        else
                            skipSpace ()
                            if i < len && query.[i] = '@' then
                                i <- i + 1
                                let start = i
                                while i < len && Char.IsDigit query.[i] do
                                    i <- i + 1
                                match Int32.TryParse(query.Substring(start, i - start)) with
                                | true, n -> Some n
                                | _ -> None
                            else
                                None

                    acc.Add(op, BPhrase(phraseWords phrase, proximity))
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

let private ngramPrefixWeight documentCount frequency =
    let documents = float documentCount
    if frequency = documents then idfFloor else log10 (documents / frequency)

let private prefixWeight (view: ReadView<'id>) key (posting: PrefixPosting<'id>) =
    match view.Rules.Tokenizer with
    | Ngrams _ when posting.GramDocumentCount > 0L ->
        let retained =
            match Map.tryFind key view.Deleted.Prefixes with
            | Some deleted ->
                // The single, unmodified prefix path subtracts one per deleted
                // document after totaling the postings from all matching grams.
                deleted |> Map.fold (fun count _ grams -> count + grams - 1L) 0L
            | None -> 0L
        let frequency = float (posting.GramDocumentCount + retained)
        ngramPrefixWeight view.DocumentCount frequency
    | _ -> idf view posting.Rows.Count

let private restrictScores (candidateIds: Set<'id> option) (scores: Map<'id, 'value>) =
    match candidateIds with
    | None -> scores
    | Some candidates ->
        candidates
        |> Seq.choose (fun id -> Map.tryFind id scores |> Option.map (fun score -> id, score))
        |> Map.ofSeq

let private cachedAccentPrefixGrams (key: string) (view: ReadView<'id>) =
    // Hex-encoded ICU sort keys delimit primary and secondary weights with 01.
    let primaryLength (key: string) =
        let mutable endOfPrimary = 0
        while endOfPrimary + 1 < key.Length
              && (key.[endOfPrimary] <> '0' || key.[endOfPrimary + 1] <> '1') do
            endOfPrimary <- endOfPrimary + 2
        endOfPrimary
    let primary = key.Substring(0, primaryLength key)
    let comparePrefix gram spelling =
        let prefixes = prefixKeysFor true view.Collation { Key = gram; Text = spelling; ByteOffset = 0L }
        if Array.contains key prefixes then 0
        else
            prefixes
            |> Array.fold (fun matching candidate ->
                if primaryLength candidate = primary.Length
                   && candidate.StartsWith(primary, StringComparison.Ordinal)
                   && (matching |> Option.forall (fun previous -> String.CompareOrdinal(candidate, previous) > 0)) then
                    Some candidate
                else matching) None
            |> Option.defaultValue gram
            |> fun prefix -> String.CompareOrdinal(key, prefix)

    FullTextCache.contiguousPrefixOrder comparePrefix view.CachedTerms

let private accentPrefixPosting diskOnly (term: Token) (view: ReadView<'id>) =
    let through = defaultArg view.FlushedThrough Int64.MaxValue
    let readGram cache gram =
        let rows = view.Postings |> Map.tryFind gram |> Option.defaultValue Map.empty
        let deleted = view.Deleted.Words |> Map.tryFind gram |> Option.defaultValue Map.empty
        match view.FlushedThrough with
        | Some boundary when boundary > 0L ->
            let accepts order = (order > boundary) = cache
            Map.filter (fun id _ -> accepts view.Documents.[id].Order) rows,
            Map.filter (fun order _ -> accepts order) deleted
        | _ -> rows, deleted

    let cached =
        if diskOnly || view.FlushedThrough.IsNone then Seq.empty
        else
            cachedAccentPrefixGrams term.Key view
            |> Seq.map (readGram true)

    let flushed =
        if through = 0L || FullTextCache.isEmpty view.FlushedAccentTerms then Seq.empty
        else
            let delimiter (key: string) =
                let mutable i = 0
                while not (key.AsSpan(i, 4).SequenceEqual("0000".AsSpan())) do
                    i <- i + 4
                i

            let query = Uca0900Weights.accentSensitiveKey term.Text
            let split = delimiter query
            let primary = query.Substring(0, split)
            let secondary = query.Substring(split + 4)
            let firstPartitionEnd = "41"
            let matches (key: string) =
                let split = delimiter key
                // With no primary weight, the query seeks the first auxiliary
                // partition and cannot match terms in later partitions.
                (primary.Length > 0 || split = 0
                 || String.CompareOrdinal(key, 0, firstPartitionEnd, 0, 2) < 0)
                && key.AsSpan(0, split).StartsWith(primary.AsSpan(), StringComparison.Ordinal)
                && key.AsSpan(split + 4).StartsWith(secondary.AsSpan(), StringComparison.Ordinal)

            // The disk cursor seeks by the whole key and stops on the first
            // posting outside the prefix on either collation level.
            view.FlushedAccentTerms
            |> FullTextCache.startingAt query
            |> Seq.choose (fun (key, gram) ->
                let rows, deleted = readGram false gram
                if rows.IsEmpty && deleted.IsEmpty then None else Some(key, (rows, deleted)))
            |> Seq.takeWhile (fst >> matches)
            |> Seq.map snd

    let mutable rows = Map.empty
    let mutable total = 0L
    let mutable deleted = Map.empty
    for live, history in Seq.append cached flushed do
        total <- total + int64 live.Count
        rows <- live |> Map.fold (fun found id frequency ->
            if Map.containsKey id found then found else Map.add id frequency found) rows
        deleted <- history |> Map.fold (fun found order grams ->
            found |> Map.change order (fun count -> Some(grams + Option.defaultValue 0L count))) deleted
    { Rows = rows; GramDocumentCount = total }, (deleted |> Map.values |> Seq.sum), int64 deleted.Count

let private prefixRowsWithCacheOrder candidateIds key (view: ReadView<'id>) (rows: Map<'id, int>) =
    match view.FlushedThrough with
    | Some through when not rows.IsEmpty ->
        let comparePrefix gramKey text =
            if prefixKeys view.Collation { Key = gramKey; Text = text; ByteOffset = 0L } |> Array.contains key then 0
            else String.CompareOrdinal(key, gramKey)

        match FullTextCache.prefixOrder comparePrefix view.CachedTerms with
        | _ :: _ :: _ as grams ->
            let rows = restrictScores candidateIds rows
            let frequencies = Collections.Generic.Dictionary<'id, int>()
            let record id frequency =
                if view.Documents.[id].Order > through then
                    frequencies.TryAdd(id, frequency) |> ignore

            for gram in grams do
                match Map.tryFind gram view.Postings with
                | Some posting when posting.Count <= rows.Count ->
                    posting |> Map.iter (fun id frequency ->
                        if rows.ContainsKey id then record id frequency)
                | Some posting ->
                    rows |> Map.iter (fun id _ -> Map.tryFind id posting |> Option.iter (record id))
                | None -> ()

            rows |> Map.map (fun id frequency ->
                match frequencies.TryGetValue id with
                | true, first -> first
                | _ -> frequency)
        | _ -> rows
    | _ -> rows

let private weightedPrefixRows candidateIds (term: Token) (view: ReadView<'id>) =
    if usesAccentSensitivePrefixes view.Collation view.Rules.Tokenizer then
        let posting, retained, deleted = accentPrefixPosting false term view
        let frequency = float (posting.GramDocumentCount + retained - deleted)
        Some(posting.Rows, ngramPrefixWeight view.DocumentCount frequency)
    else
        Map.tryFind term.Key view.PrefixPostings
        |> Option.map (fun posting -> prefixRowsWithCacheOrder candidateIds term.Key view posting.Rows, prefixWeight view term.Key posting)

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
        match weightedPrefixRows candidateIds term view with
        | None -> Map.empty
        | Some(rows, weight) ->
            rows
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

type private NgramLookup =
    | ExactNgram of string
    | NgramPrefix of string

let private ngramLookupKey = function
    | ExactNgram key | NgramPrefix key -> key

type private NgramMatch =
    { Terms: Set<string>
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

        let isPhrase = function
            // A one-gram phrase is represented as an exact word lookup.
            | BPhrase _ | BWord(_, false) -> true
            | _ -> false

        let frequencies = leaves nodes |> Seq.collect (snd >> keys) |> Seq.countBy id |> Map.ofSeq
        let sharedKeys =
            frequencies |> Map.keys |> Seq.choose (function
                | ExactNgram key when frequencies.ContainsKey(NgramPrefix key) -> Some key
                | _ -> None) |> Set.ofSeq

        let tracksPostingBounds =
            let canVisitBoundedPhrase =
                (nodes |> List.exists (function _, BGroup _ -> true | _ -> false))
                || (view.FlushedThrough.IsSome && (nodes |> List.exists (fun (op, term) -> op = Must && isPhrase term)))
            view.HasDocumentOrder && canVisitBoundedPhrase
            && (leaves nodes |> Seq.exists (function Must, BWord(_, true) -> true | _ -> false))

        let weighted = hasRankAdjustments nodes
        let accentSensitive = usesAccentSensitivePrefixes view.Collation view.Rules.Tokenizer
        let repeated = not sharedKeys.IsEmpty || (frequencies |> Map.exists (fun _ count -> count > 1))
        let cacheSensitive =
            view.FlushedThrough.IsSome
            && (frequencies |> Map.exists (fun key _ -> match key with NgramPrefix _ -> true | _ -> false))
            && (leaves nodes |> Seq.exists (snd >> isPhrase))
        let deletedSensitive =
            match nodes with
            | [ Optional, BWord(_, true) ] -> false
            | _ ->
                not view.Deleted.Documents.IsEmpty
                && (frequencies |> Map.exists (fun key _ ->
                    match key with
                    | ExactNgram word -> view.Deleted.Words.ContainsKey word
                    | NgramPrefix prefix -> accentSensitive || view.Deleted.Prefixes.ContainsKey prefix))

        if not tracksPostingBounds && not repeated && not weighted && not cacheSensitive && not deletedSensitive then
            None
        else
            // A wildcard exclusion visits postings only if its group has matches.
            // When its key recurs, that decision uses the full index result.
            let fullScope =
                leaves nodes
                |> Seq.exists (function
                    | MustNot, BWord(word, true) -> sharedKeys.Contains word.Key || frequencies.[NgramPrefix word.Key] > 1
                    | _ -> false)

            // In a weighted query, a repeated gram can get its first position
            // from a document outside the SQL candidates.
            let needsCorpusOrder = weighted && repeated && frequencies.Count > 1
            let needsVisibleCorpus = tracksPostingBounds || needsCorpusOrder || cacheSensitive
            let scope =
                if needsVisibleCorpus then view.VisibleDocumentIds
                elif fullScope then None
                else candidateIds
            let documentCounts = Collections.Generic.Dictionary<string, int64>()
            let sharedRowFrequencies = Collections.Generic.Dictionary<string, Collections.Generic.Dictionary<'id, int>>()
            let exactSegments = Collections.Generic.Dictionary<string, Map<'id, int> * Map<'id, int>>()
            let diskPrefixes = Collections.Generic.Dictionary<string, PrefixPosting<'id> option>()
            let accentPrefixes = Collections.Generic.Dictionary<struct (bool * string), PrefixPosting<'id> * int64>()
            let scoringPrefixes = Collections.Generic.Dictionary<string, Map<'id, int>>()
            let contributionOrder = Collections.Generic.Dictionary<string, int>()
            let mutable postingBounds = None
            let mutable inPhraseMode = false

            let countDocuments key frequency =
                let _, count = documentCounts.TryGetValue key
                documentCounts.[key] <- count + frequency

            let rememberRowFrequencies key rows =
                // Exact and prefix lookups share rank state by collation key.
                // The first posting read for a document supplies its term frequency.
                let first =
                    match sharedRowFrequencies.TryGetValue key with
                    | true, first -> first
                    | _ ->
                        let first = Collections.Generic.Dictionary<'id, int>()
                        sharedRowFrequencies.Add(key, first)
                        first
                rows |> Map.iter (fun id frequency -> first.TryAdd(id, frequency) |> ignore)

            let accentPrefix diskOnly (term: Token) =
                let key = struct (diskOnly, term.Text)
                match accentPrefixes.TryGetValue key with
                | true, value -> value
                | _ ->
                    let posting, retained, _ = accentPrefixPosting diskOnly term view
                    let value = posting, retained
                    accentPrefixes.Add(key, value)
                    value

            let retainedPrefixFrequency diskOnly key =
                match Map.tryFind key view.Deleted.Prefixes with
                | None -> 0L
                | Some history ->
                    match diskOnly, view.FlushedThrough with
                    | true, Some through ->
                        history |> Map.fold (fun count order grams ->
                            if order <= through then count + grams else count) 0L
                    | _ -> history |> Map.values |> Seq.sum

            let prefixPosting diskOnly key =
                let all = Map.tryFind key view.PrefixPostings
                match diskOnly, view.FlushedThrough with
                | true, Some through when through = 0L -> None
                | true, Some through ->
                    match diskPrefixes.TryGetValue key with
                    | true, posting -> posting
                    | _ ->
                        let posting =
                            all |> Option.map (fun posting ->
                                let rows = posting.Rows |> Map.filter (fun id _ -> view.Documents.[id].Order <= through)
                                let frequency =
                                    rows |> Map.fold (fun count id _ ->
                                        let document = view.Documents.[id].Content
                                        let grams =
                                            indexedTokens document
                                            |> Array.filter (fun gram -> prefixKeys view.Collation gram |> Array.contains key)
                                            |> Array.distinctBy _.Key
                                        count + int64 grams.Length) 0L
                                { Rows = rows; GramDocumentCount = frequency })
                        diskPrefixes.Add(key, posting)
                        posting
                | _ -> all

            let documentBounds rows =
                let order id = view.Documents.[id].Order
                if view.DocumentOrderFollowsIds then
                    order (fst (Map.minKeyValue rows)), order (fst (Map.maxKeyValue rows))
                else
                    rows |> Map.fold (fun (lower, upper) id _ ->
                        let position = order id
                        min lower position, max upper position) (Int64.MaxValue, 0L)

            let boundedExactPosting lower upper key (rows: Map<'id, int>) (deleted: Map<int64, int64>) =
                let overlaps rows deleted =
                    let first, last =
                        if Map.isEmpty rows then Int64.MaxValue, 0L else documentBounds rows
                    let first, last =
                        if Map.isEmpty deleted then first, last
                        else min first (fst (Map.minKeyValue deleted)), max last (fst (Map.maxKeyValue deleted))
                    first <= upper && last >= lower

                let readCache cache deletedCache =
                    match Map.tryFind key view.CachedPostingTails with
                    | Some tail when not tail.EarlierSegmentEnds.IsEmpty ->
                        let order id = view.Documents.[id].Order
                        let orders = Seq.append (Map.keys cache |> Seq.map order) (Map.keys deletedCache)
                        let struct (first, last, frequency) =
                            FullTextCache.boundedSegments lower upper tail.EarlierSegmentEnds orders
                        let available =
                            cache |> Map.filter (fun id _ ->
                                let position = order id
                                first <= position && position <= last)
                        frequency, available, frequency > 0L
                    | _ ->
                        if overlaps cache deletedCache then int64 cache.Count + int64 deletedCache.Count, cache, true
                        else 0L, Map.empty, false

                match view.FlushedThrough with
                | None ->
                    let read = overlaps rows deleted
                    int64 rows.Count + int64 deleted.Count, (if read then rows else Map.empty), read
                | Some 0L -> readCache rows deleted
                | Some through ->
                    let disk, cache =
                        match exactSegments.TryGetValue key with
                        | true, segments -> segments
                        | _ ->
                            let segments = rows |> Map.partition (fun id _ -> view.Documents.[id].Order <= through)
                            exactSegments.Add(key, segments)
                            segments
                    let deletedDisk, deletedCache = deleted |> Map.partition (fun order _ -> order <= through)
                    let readDisk = overlaps disk deletedDisk
                    let cacheFrequency, available, readCached = readCache cache deletedCache
                    // Disk counts all postings, and cache counts only the segments it reads.
                    let frequency = int64 disk.Count + int64 deletedDisk.Count + cacheFrequency
                    if readDisk then
                        let available =
                            if available.Count = cache.Count then rows
                            else available |> Map.fold (fun combined id count -> Map.add id count combined) disk
                        frequency, available, true
                    else frequency, available, readCached

            let visitExactWords bounds found =
                let rec visit offset candidates =
                    if offset = Array.length found then Some candidates
                    else
                        match found.[offset] with
                        | NgramPrefix _ -> visit (offset + 1) candidates
                        | ExactNgram word ->
                            let rows = Map.tryFind word view.Postings |> Option.defaultValue Map.empty
                            let deleted = Map.tryFind word view.Deleted.Words |> Option.defaultValue Map.empty
                            let frequency, available, read =
                                match bounds with
                                | Some(lower, upper) -> boundedExactPosting lower upper word rows deleted
                                | None ->
                                    let frequency = int64 rows.Count + int64 deleted.Count
                                    frequency, rows, frequency > 0L
                            countDocuments word frequency
                            if not available.IsEmpty && sharedKeys.Contains word then
                                rememberRowFrequencies word (restrictScores scope available)
                            match bounds with
                            // MySQL stops a phrase at the first unread posting.
                            // Later grams must not change another phrase's rank.
                            | _ when not read -> None
                            | Some _ when available.Count < rows.Count ->
                                let allowed = available |> Map.keys |> Set.ofSeq
                                visit (offset + 1) (intersectCandidates candidates (Some allowed))
                            | _ -> visit (offset + 1) candidates

                visit 0 scope

            let adjustmentSteps term id =
                match term with
                | BWord(word, true) ->
                    indexedTokens view.Documents.[id].Content
                    |> Seq.filter (prefixKeysFor (accentSensitive && view.FlushedThrough = Some 0L) view.Collation >> Array.contains word.Key)
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
                    if isPhrase term then inPhraseMode <- true
                    let found = keys term
                    let matches =
                        match term with
                        | BWord(word, true) ->
                            // Required prefixes use the cache even after visiting a phrase.
                            let diskOnly = inPhraseMode && not isRequired
                            let posting, retained =
                                if accentSensitive then
                                    let posting, retained = accentPrefix diskOnly word
                                    Some posting, retained
                                else
                                    prefixPosting diskOnly word.Key, retainedPrefixFrequency diskOnly word.Key
                            let frequency = posting |> Option.map _.GramDocumentCount |> Option.defaultValue 0L
                            countDocuments word.Key (frequency + retained)
                            match posting with
                            | None -> Map.empty
                            | Some posting ->
                                if accentSensitive || sharedKeys.Contains word.Key then
                                    let rows =
                                        if accentSensitive then restrictScores scope posting.Rows
                                        else prefixRowsWithCacheOrder scope word.Key view posting.Rows
                                    rememberRowFrequencies word.Key rows
                                posting.Rows |> restrictScores scope |> Map.map (fun _ _ -> 0.0)
                        | _ ->
                            let bounds = if isRequired then postingBounds else None
                            match visitExactWords bounds found with
                            | Some candidates -> evalTerm candidates view term
                            | None -> Map.empty
                    let terms = found |> Array.map ngramLookupKey |> Set.ofArray
                    match contributes with
                    | Some accepts when
                        (terms |> Set.exists (contributionOrder.ContainsKey >> not))
                        && (matches |> Map.exists (fun id _ -> accepts id)) ->
                        // A gram gets its position when it first contributes to a document.
                        for lookup in found do
                            let key = ngramLookupKey lookup
                            contributionOrder.TryAdd(key, contributionOrder.Count) |> ignore
                    | _ -> ()
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

            let scoreTerm id key =
                let frequency = float documentCounts.[key]
                let occurrences, weight =
                    if sharedKeys.Contains key || (accentSensitive && frequencies.ContainsKey(NgramPrefix key)) then
                        sharedRowFrequencies.[key].[id], ngramPrefixWeight view.DocumentCount frequency
                    elif frequencies.ContainsKey(NgramPrefix key) then
                        let rows =
                            match scoringPrefixes.TryGetValue key with
                            | true, rows -> rows
                            | _ ->
                                let rows = prefixRowsWithCacheOrder candidateIds key view view.PrefixPostings.[key].Rows
                                scoringPrefixes.Add(key, rows)
                                rows
                        rows.[id], ngramPrefixWeight view.DocumentCount frequency
                    else
                        view.Postings.[key].[id], wordFrequencyWeight view frequency

                float occurrences * weight * weight

            matchNodes nodes
            |> (if fullScope || needsVisibleCorpus then restrictScores candidateIds else id)
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
                weightedPrefixRows candidateIds term view
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
    let query = fullTextQueryText view.Collation query
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
        | None -> naturalScoresForQueryText None view query

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
        |> sumTermScoresWithin (indexedWordWeight view) candidateIds view

let expansionScores (index: Index<'id>) (query: string) : Map<'id, float> =
    expansionScoresInView None (readView index) query

let internal expansionScoresWithin (candidateIds: Set<'id>) (index: Index<'id>) (query: string) =
    expansionScoresInView (Some candidateIds) (readView index) query

let expansionScoresOf (corpus: Corpus) (query: string) : float[] =
    expansionScores corpus.Index query |> scoresInCorpusOrder corpus
