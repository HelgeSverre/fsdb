module Fsdb.Tests.FullTextTests

open Expecto
open Fsdb.FullText
open Fsdb.Collation

/// The manual's own `articles` corpus (title and body concatenated per
/// row, the way a `(title,body)` FULLTEXT index scores) — every expected
/// value below was read off a live MySQL 8.4.11 running the same data.
let private articles =
    [ "MySQL Tutorial DBMS stands for DataBase Management System ..."
      "How To Use MySQL Well After you went through a ..."
      "Optimizing MySQL In this tutorial, we show ..."
      "1001 MySQL Tricks 1. Never run mysqld as root. 2. ..."
      "MySQL vs. YourSQL In the following database comparison ..."
      "MySQL Security When configured properly, MySQL ..." ]

let private corpus = buildCorpus articles

let private closeTo (expected: float) (actual: float) (label: string) =
    Expect.isTrue (abs (actual - expected) <= max 1e-9 (abs expected * 1e-3)) (sprintf "%s: expected ~%.12g, got %.12g" label expected actual)

let tests =
    testList
        "fulltext"
        [ testCase "nonbinary full-text collations fold case for words and ngrams"
          <| fun _ ->
              let collation = tryFind "utf8mb4_0900_as_cs" |> Option.get
              for tokenizer in [ Words; Ngrams 2 ] do
                  let rules = { Tokenizer = tokenizer; Stopwords = StopwordPolicy.Disabled }
                  let index =
                      buildIndexWithDocumentSettings rules collation
                          [ 1, rules, [ "orchard cobalt" ]; 2, rules, [ "Orchard Cobalt" ]
                            3, rules, [ "ORCHARD COBALT" ]; 4, rules, [ "zzzz" ] ]
                  for query in [ "orchard"; "Orchard"; "ORCHARD"; "\"orchard cobalt\""; "\"Orchard Cobalt\"" ] do
                      for scores in [ naturalScores index query; booleanScores index query; expansionScores index query ] do
                          Expect.equal (scores |> Map.keys |> Seq.toList) [ 1; 2; 3 ] "nonbinary full-text matching folds case in every mode"
                  for query in [ "orch*"; "Orch*"; "ORCH*" ] do
                      Expect.equal (booleanScores index query |> Map.keys |> Seq.toList) [ 1; 2; 3 ] "prefix postings use normalized text"
                  let changed = index |> removeDocument 2 |> addDocument 3 "violet violet"
                  Expect.equal (booleanScores changed "ORCH*" |> Map.keys |> Seq.toList) [ 1 ] "mutation removes normalized prefixes"
                  Expect.equal (naturalScores changed "Orchard" |> Map.keys |> Seq.toList) [ 1 ] "mutation removes normalized exact postings"

          testCase "case-sensitive nonbinary full-text retains accent sensitivity"
          <| fun _ ->
              let collation = tryFind "utf8mb4_0900_as_cs" |> Option.get
              let index = buildIndexWith collation [ 1, "cafe"; 2, "café"; 3, "CAFÉ"; 4, "zzzz" ]
              for score in [ naturalScores; booleanScores; expansionScores ] do
                  Expect.equal (score index "CAFE" |> Map.keys |> Seq.toList) [ 1 ] "case folding retains unaccented spelling"
                  Expect.equal (score index "café" |> Map.keys |> Seq.toList) [ 2; 3 ] "accented spellings share a case-folded posting"
              for scores in
                  [ tryNaturalSingleTermScoresDictionaryInView None (readView index) "CAFÉ"
                    tryFlatBooleanScoresDictionaryInView None (readView index) "CAFÉ" ] do
                  Expect.equal ((Option.get scores).Keys |> Seq.sort |> Seq.toList) [ 2; 3 ] "optimized lookups normalize case"
              closeTo (log10 2.0 ** 2.0) (naturalScores index "CAFÉ").[2] "case variants share document frequency"

          testCase "word lookups use indexed collation equivalents of stopped spellings"
          <| fun _ ->
              let index = buildIndexWith defaultCollation [ 1, "the"; 2, "thé"; 3, "THE"; 4, "tHe"; 5, "zzzz" ]
              for query in [ "the"; "thé"; "THE"; "the the" ] do
                  for scores in [ naturalScores index query; booleanScores index query; expansionScores index query ] do
                      Expect.equal (scores |> Map.keys |> Seq.toList) [ 2 ] "only the indexed accented spelling contributes a posting"
              closeTo (log10 5.0 ** 2.0) (naturalScores index "the").[2] "filtered spellings do not inflate document frequency"
              for scores in
                  [ tryNaturalSingleTermScoresDictionaryInView None (readView index) "the"
                    tryFlatBooleanScoresDictionaryInView None (readView index) "the" ] do
                  Expect.equal ((Option.get scores).Keys |> Seq.toList) [ 2 ] "optimized lookups obey the same posting boundary"
              Expect.equal (naturalScores index "\"the zzzz\"" |> Map.keys |> Seq.toList) [ 5 ] "phrase anchors still omit a leading stopword"
              Expect.equal (booleanScores index "th*" |> Map.keys |> Seq.toList) [ 2 ] "prefixes omit literal stopped documents"

          testCase "word lookups retain old postings after active stopwords change"
          <| fun _ ->
              let unfiltered = { Tokenizer = Words; Stopwords = StopwordPolicy.Disabled }
              let filtered = { unfiltered with Stopwords = StopwordPolicy.BuiltIn }
              let index =
                  buildIndexWithDocumentSettings unfiltered defaultCollation [ 1, unfiltered, [ "the" ]; 2, unfiltered, [ "zzzz" ] ]
                  |> withIndexingRules filtered
                  |> addDocument 3 "the"
              for scores in [ naturalScores index "the"; booleanScores index "the"; expansionScores index "the" ] do
                  Expect.equal (scores |> Map.keys |> Seq.toList) [ 1 ] "new filtering does not hide or recreate historical postings"
              Expect.isEmpty (naturalScores (removeDocument 1 index) "the") "removing the historical document does not expose a filtered newer document"

          testCase "document indexing rules survive reconstruction and active policy changes"
          <| fun _ ->
              let filtered = { Tokenizer = Ngrams 2; Stopwords = StopwordPolicy.BuiltIn }
              let unfiltered = { filtered with Stopwords = StopwordPolicy.Disabled }
              let longer = { unfiltered with Tokenizer = Ngrams 3 }
              let documents =
                  [ 1, unfiltered, [ "ab" ]; 2, filtered, [ "ab" ]
                    3, longer, [ "abc" ]; 4, filtered, [ "zz" ] ]
              let index = buildIndexWithDocumentSettings filtered defaultCollation documents
              Expect.equal (naturalScores index "ab" |> Map.keys |> Seq.toList) [ 1 ] "only historically indexed grams match"
              Expect.equal (booleanScores index "a*" |> Map.keys |> Seq.toList) [ 1; 3 ] "prefixes retain each document's rules"
              let changed = index |> withIndexingRules unfiltered |> addDocument 5 "ab"
              Expect.equal (documentRules 2 changed) (Some filtered) "changing active rules leaves stored document rules intact"
              Expect.equal (documentRules 5 changed) (Some unfiltered) "new writes capture active rules"
              Expect.equal (naturalScores changed "ab" |> Map.keys |> Seq.toList) [ 1; 5 ] "new policy does not reinterpret older filtered documents"
              let saved =
                  documents @ [ 5, unfiltered, [ "ab" ] ]
                  |> List.map (fun (id, _, fields) -> id, documentRules id changed |> Option.get, fields)
              let restored = buildIndexWithDocumentSettings (activeRules changed) defaultCollation saved
              Expect.equal (naturalScores restored "ab") (naturalScores changed "ab") "reconstructed postings retain historical rules"
              Expect.equal (booleanScores (removeDocument 1 restored) "a*" |> Map.keys |> Seq.toList) [ 3; 5 ] "removal uses the removed document's rules"
              Expect.equal (activeRules index) filtered "old immutable index retains its active rules"

          testCase "transaction merge retains source document rules and target write rules"
          <| fun _ ->
              let filtered = { Tokenizer = Ngrams 2; Stopwords = StopwordPolicy.BuiltIn }
              let unfiltered = { filtered with Stopwords = StopwordPolicy.Disabled }
              let baseline = buildIndexWithDocumentSettings filtered defaultCollation [ 1, filtered, [ "zz" ] ]
              let source = baseline |> withIndexingRules unfiltered |> addDocument 1 "ab"
              let target = baseline |> addDocument 2 "ab"
              let merged = mergeDocuments Map.empty baseline source target
              Expect.equal (documentRules 1 merged) (Some unfiltered) "merged document keeps source indexing rules"
              Expect.equal (documentRules 2 merged) (Some filtered) "concurrent document keeps target indexing rules"
              Expect.equal (activeRules merged) filtered "publication does not replace the target's active rules"
              Expect.equal (naturalScores merged "ab" |> Map.keys |> Seq.toList) [ 1 ] "only the unfiltered source document has the posting"

          testCase "stopword policy applies to word and ngram queries and later writes"
          <| fun _ ->
              for tokenizer, term in [ Words, "the"; Ngrams 2, "ab" ] do
                  let documents = [ 1, [ term ]; 2, [ "zzzz" ]; 3, [ term ] ]
                  let enabled = buildIndexWithStopwords StopwordPolicy.BuiltIn tokenizer defaultCollation documents
                  let disabled = buildIndexWithStopwords StopwordPolicy.Disabled tokenizer defaultCollation documents
                  for scores in [ naturalScores enabled term; booleanScores enabled term; expansionScores enabled term ] do
                      Expect.isEmpty scores "the built-in policy excludes the stopword"
                  for scores in [ naturalScores disabled term; booleanScores disabled term; expansionScores disabled term ] do
                      Expect.equal (scores |> Map.keys |> Seq.toList) [ 1; 3 ] "disabled stopwords remain searchable"
                  for scores in
                      [ tryNaturalSingleTermScoresDictionaryWithin None disabled term
                        tryFlatBooleanScoresDictionaryWithin None disabled term ] do
                      Expect.equal (scores |> Option.get |> _.Keys |> Seq.sort |> Seq.toList) [ 1; 3 ] "optimized scoring retains the index policy"
                  let changed = disabled |> addDocument 4 term |> removeDocument 1
                  Expect.equal (booleanScores changed term |> Map.keys |> Seq.toList) [ 3; 4 ] "later writes retain the policy"
                  Expect.equal (booleanScores changed (term + "*") |> Map.keys |> Seq.toList) [ 3; 4 ] "prefix maintenance uses the document policy"
                  Expect.equal (booleanScores disabled term |> Map.keys |> Seq.toList) [ 1; 3 ] "writes preserve the previous index"

          testCase "disabling stopwords does not disable word length rules"
          <| fun _ ->
              let index = buildIndexWithStopwords StopwordPolicy.Disabled Words defaultCollation [ 1, [ "a the orchard" ]; 2, [ "orchard" ] ]
              Expect.isEmpty (naturalScores index "a") "minimum token length still applies"
              Expect.equal (naturalScores index "the" |> Map.keys |> Seq.toList) [ 1 ] "the length-eligible stopword is searchable"

          testCase "document merging rebases inserted identities without replacing concurrent postings"
          <| fun _ ->
              let baseline = buildIndexWith defaultCollation [ 1, "orchard"; 2, "cobalt" ]
              let branch = baseline |> removeDocument 1 |> addDocument 3 "meadow"
              let concurrent = baseline |> addDocument 3 "forest"
              let merged = mergeDocuments (Map.ofList [ 3, 4 ]) baseline branch concurrent
              Expect.isEmpty (naturalScores merged "orchard") "the branch deletion is published"
              Expect.equal (naturalScores merged "meadow" |> Map.keys |> Seq.toList) [ 4 ] "the inserted document uses its rebased row ID"
              Expect.equal (naturalScores merged "forest" |> Map.keys |> Seq.toList) [ 3 ] "the concurrent insert retains its postings"
              Expect.isTrue (sameDocument 2 baseline merged) "unchanged document identity survives"
              Expect.isTrue (sameDocument 3 concurrent merged) "concurrent document identity survives"

          testCase "read visibility retains committed term frequencies for word and ngram scores"
          <| fun _ ->
              for tokenizer, term, other in [ Words, "orchard", "cobalt"; Ngrams 2, "生日", "中文" ] do
                  let index = buildIndexWithTokenizer tokenizer defaultCollation [ 1, term; 2, term; 3, other ]
                  let view = readView index |> restrictReadView (Set.ofList [ 2; 3 ])
                  let expected = log10(3.0 / 2.0) ** 2.0
                  for scores in [ naturalScoresInView None view term; booleanScoresInView None view term ] do
                      Expect.equal (scores |> Map.keys |> Seq.toList) [ 2 ] "only the unchanged committed document is visible"
                      closeTo expected scores.[2] "hidden postings still contribute to document frequency"
                  for scores in
                      [ tryNaturalSingleTermScoresDictionaryInView None view term
                        tryFlatBooleanScoresDictionaryInView None view term ] do
                      let scores = scores |> Option.get
                      Expect.equal (scores.Keys |> Seq.toList) [ 2 ] "dictionary paths use the same visibility"
                      closeTo expected scores.[2] "dictionary relevance"
                  Expect.isEmpty (naturalScoresInView (Some(Set.singleton 1)) view term) "predicate candidates cannot restore a hidden document"
                  Expect.equal (naturalScores index term |> Map.keys |> Seq.toList) [ 1; 2 ] "the commit index remains complete"

          testCase "read document counts vary independently of committed postings"
          <| fun _ ->
              let index = buildIndexWith defaultCollation [ 1, "orchard"; 2, "orchard"; 3, "cobalt" ]
              let inserted = readView index |> withReadDocumentCount 4
              closeTo (log10(4.0 / 2.0) ** 2.0) (naturalScoresInView None inserted "orchard").[1] "pending rows can affect population without adding postings"
              Expect.equal (naturalScoresInView None inserted "orchard" |> Map.keys |> Seq.toList) [ 1; 2 ] "only committed documents have scores"
              closeTo (log10(3.0 / 2.0) ** 2.0) (naturalScores index "orchard").[1] "read statistics do not alter the stored index"

          testCase "query expansion excludes hidden seeds without restricting seeds to predicate candidates"
          <| fun _ ->
              let index =
                  buildIndexWith defaultCollation
                      [ 1, "orchard orchard forbidden"
                        2, "orchard meadow"
                        3, "forbidden signal"
                        4, "meadow trail"
                        5, "cobalt blue" ]
              let view = readView index |> restrictReadView (Set.ofList [ 2; 3; 4; 5 ])
              for query in [ "orchard"; "orchard orchard"; "\"orchard\"" ] do
                  Expect.equal (expansionScoresInView None view query |> Map.keys |> Seq.toList) [ 2; 4 ] "hidden seed terms cannot introduce a match"
                  Expect.equal
                      (expansionScoresInView (Some(Set.singleton 4)) view query |> Map.keys |> Seq.toList)
                      [ 4 ]
                      "visible seed 2 can expand a query whose final predicate admits only row 4"

          testCase "tokenizer: word chars, in-word apostrophes, case folding, punctuation"
          <| fun _ ->
              Expect.equal (tokenize "Never run mysqld as root!") [| "never"; "run"; "mysqld"; "as"; "root" |] "plain words"
              Expect.equal (tokenize "O'Brien's DB_2, 'quoted'") [| "o'brien's"; "db_2"; "quoted" |] "apostrophes and underscore"
              Expect.equal (tokenize "") [||] "empty"

          testCase "ngram size changes retain historical postings during writes"
          <| fun _ ->
              let original = buildIndexWithTokenizer (Ngrams 2) defaultCollation [ 1, "生日快乐"; 2, "生日" ]
              let changed = original |> withTokenizer (Ngrams 3) |> addDocument 3 "生日快乐"
              let ids scores = scores |> Map.keys |> Seq.toList
              Expect.equal (naturalScores changed "生日快" |> ids) [ 3 ] "new writes use the new size"
              Expect.equal (booleanScores changed "生日" |> ids) [ 1; 2 ] "short boolean words can find historical postings"
              Expect.equal (booleanScores changed "\"生日\"" |> ids) [ 1; 2 ] "quoted short words find historical postings"
              let deleted = changed |> removeDocument 2
              Expect.equal (booleanScores deleted "生*" |> ids) [ 1; 3 ] "removal clears historical prefix postings"
              let replaced = deleted |> addDocument 1 "中文检索"
              Expect.equal (booleanScores replaced "生*" |> ids) [ 3 ] "replacement clears the old generation"
              Expect.equal (naturalScores replaced "中文检" |> ids) [ 1 ] "replacement uses the current generation"

          testCase "full-text terms follow collation case and accent sensitivity"
          <| fun _ ->
              let aiCi = tryFind "utf8mb4_0900_ai_ci" |> Option.get
              let asCi = tryFind "utf8mb4_0900_as_ci" |> Option.get
              let binary = tryFind "utf8mb4_bin" |> Option.get

              let aiCorpus = buildCorpusWith aiCi [ "résumé writing"; "smørrebrød" ]
              let asCorpus = buildCorpusWith asCi [ "résumé writing"; "smørrebrød" ]
              let binaryCorpus = buildCorpusWith binary [ "Résumé writing"; "The uncommonword" ]

              Expect.isGreaterThan (naturalScoresOf aiCorpus "resume").[0] 0.0 "ai_ci folds accents"
              Expect.isGreaterThan (naturalScoresOf aiCorpus "smorrebrod").[1] 0.0 "ICU folding covers non-combining letters"
              Expect.equal (naturalScoresOf asCorpus "resume").[0] 0.0 "as_ci preserves accents"
              Expect.isGreaterThan (naturalScoresOf asCorpus "RÉSUMÉ").[0] 0.0 "as_ci folds case"
              Expect.equal (naturalScoresOf binaryCorpus "résumé").[0] 0.0 "binary preserves case"
              Expect.equal (naturalScoresOf binaryCorpus "The").[1] 0.0 "stopwords remain case-insensitive"
              Expect.isGreaterThan (booleanScoresOf aiCorpus "+resu*").[0] 0.0 "boolean prefixes use the same folding"
              Expect.isGreaterThan (naturalScoresOf asCorpus "re\u0301sume\u0301").[0] 0.0 "canonical combining forms share a key"

          testCase "natural language: TF×IDF² matches the oracle's scores"
          <| fun _ ->
              // 'Tutorial': docs 1 and 3 contain it once; IDF = log10(6/2).
              let s = naturalScoresOf corpus "Tutorial"
              closeTo 0.22764469683170319 s.[0] "doc 1"
              closeTo 0.0 s.[1] "doc 2"
              closeTo 0.22764469683170319 s.[2] "doc 3"

          testCase "a term present in every document scores the epsilon floor, not zero"
          <| fun _ ->
              // Real MySQL: WHERE MATCH ... AGAINST ('mysql') matches all six
              // rows with a tiny positive rank, doubled where TF = 2.
              let s = naturalScoresOf corpus "mysql"
              Expect.all (List.ofArray s |> List.map (fun v -> v > 0.0)) id "every doc matches"
              closeTo (s.[0] * 2.0) s.[5] "doc 6 has TF 2"
              closeTo 1.885928302414186e-9 s.[0] "the observed epsilon"

          testCase "stopwords and short tokens are ignored"
          <| fun _ ->
              Expect.equal (naturalScoresOf corpus "the in to a") (Array.zeroCreate 6) "all stopwords"
              Expect.equal (naturalScoresOf corpus "we") (Array.zeroCreate 6) "below min token length"

              let longWord = String.replicate 85 "x"
              let longCorpus = buildCorpus [ longWord ]
              Expect.equal (naturalScoresOf longCorpus longWord) [| 0.0 |] "tokens over the InnoDB maximum are omitted"

          testCase "boolean: + requires and - excludes"
          <| fun _ ->
              let s = booleanScoresOf corpus "+MySQL -YourSQL"
              let matched = s |> Array.mapi (fun i v -> i + 1, v > 0.0) |> Array.filter snd |> Array.map fst
              Expect.equal matched [| 1; 2; 3; 4; 6 |] "YourSQL row excluded"

              let excludedOnly = booleanScoresOf corpus "-YourSQL"
              Expect.isTrue (excludedOnly |> Array.forall ((=) 0.0)) "an exclusion without a positive term matches nothing"

          testCase "boolean: > adds 1.0 and < subtracts 1.0 from a matched term's contribution"
          <| fun _ ->
              let s = booleanScoresOf corpus ">tutorial <security MySQL"
              closeTo 1.227644681930542 s.[0] "raised tutorial"
              closeTo -0.3944806456565857 s.[5] "lowered security goes negative"

          testCase "boolean: phrase, prefix wildcard, and proximity"
          <| fun _ ->
              let phrase = booleanScoresOf corpus "\"database comparison\""
              Expect.equal (phrase |> Array.mapi (fun i v -> i + 1, v > 0.0) |> Array.filter snd |> Array.map fst) [| 5 |] "exact phrase"

              let prefix = booleanScoresOf corpus "data*"
              Expect.equal (prefix |> Array.mapi (fun i v -> i + 1, v > 0.0) |> Array.filter snd |> Array.map fst) [| 1; 5 |] "prefix match"

              let shortPrefix = booleanScoresOf corpus "tu*"
              Expect.equal (shortPrefix |> Array.mapi (fun i v -> i + 1, v > 0.0) |> Array.filter snd |> Array.map fst) [| 1; 3 |] "short prefixes use indexed words"

              let near = booleanScoresOf corpus "\"stands database\" @5"
              Expect.equal (near |> Array.mapi (fun i v -> i + 1, v > 0.0) |> Array.filter snd |> Array.map fst) [| 1 |] "proximity window"

              let far = booleanScoresOf corpus "\"stands database\" @1"
              Expect.equal (far |> Array.mapi (fun i v -> i + 1, v > 0.0) |> Array.filter snd |> Array.map fst) [||] "window too narrow"

          testCase "boolean phrase and proximity ranking follows its indexed words"
          <| fun _ ->
              let phrases = buildCorpus [ "database concurrency"; "database concurrency database"; "concurrency x database" ]
              let wordScores = booleanScoresOf phrases "database concurrency"
              let exact = booleanScoresOf phrases "\"database concurrency\""
              let near = booleanScoresOf phrases "\"database concurrency\" @3"

              Expect.equal exact.[0] wordScores.[0] "an exact match retains ordinary word relevance"
              Expect.equal exact.[1] wordScores.[1] "term frequency still affects phrase relevance"
              Expect.equal exact.[2] 0.0 "an exact phrase remains ordered and adjacent"
              Expect.equal near wordScores "proximity is unordered and scores every matching word"

              let boundaries = buildCorpus [ "database concurrency"; "database x concurrency"; "database x y concurrency"; "concurrency x database" ]
              let matches query =
                  booleanScoresOf boundaries query
                  |> Array.map (fun score -> score > 0.0)

              Expect.equal (matches "\"database concurrency\" @1") [| false; false; false; false |] "the distance bound is strict"
              Expect.equal (matches "\"database concurrency\" @2") [| true; false; false; false |] "adjacent words span one position"
              Expect.equal (matches "\"database concurrency\" @3") [| true; true; false; true |] "word order does not affect proximity"

              let duplicates = buildCorpus [ "database"; "database database" ]
              Expect.equal
                  (booleanScoresOf duplicates "\"database database\" @0" |> Array.map (fun score -> score > 0.0))
                  [| true; true |]
                  "duplicate query words share one proximity requirement"

          testCase "boolean phrases retain internal stopword and short-token positions"
          <| fun _ ->
              let phrases =
                  buildCorpus
                      [ "the database comparison"
                        "database the comparison"
                        "database comparison"
                        "da database comparison"
                        "database da comparison" ]

              let matches query =
                  booleanScoresOf phrases query
                  |> Array.mapi (fun index score -> index + 1, score > 0.0)
                  |> Array.filter snd
                  |> Array.map fst

              Expect.equal (matches "\"the database\"") [| 1; 2; 3; 4; 5 |] "leading stopword is not a phrase anchor"
              Expect.equal (matches "\"database the comparison\"") [| 2 |] "internal stopword retains its position"
              Expect.equal (matches "\"da database\"") [| 1; 2; 3; 4; 5 |] "leading short token is not a phrase anchor"
              Expect.equal (matches "\"database da comparison\"") [| 5 |] "internal short token retains its position"
              Expect.equal (matches "\"the\"") [||] "a phrase without a searchable term matches nothing"

              let quotedStar = buildCorpus [ "database comparison"; "data comparison" ]
              let scores = booleanScoresOf quotedStar "\"data* comparison\""
              Expect.equal (scores |> Array.map (fun score -> score > 0.0)) [| false; true |] "star remains punctuation inside a phrase"

          testCase "boolean: ~ keeps the row but zeroes the term's contribution"
          <| fun _ ->
              let s = booleanScoresOf corpus "~security MySQL"
              // Doc 6 contains security, but its score is just mysql's 2ε.
              closeTo (s.[0] * 2.0) s.[5] "soft negation contributes nothing"
              Expect.all (List.ofArray s |> List.map (fun v -> v > 0.0)) id "no row excluded"

          testCase "a required boolean stopword can never match — the index doesn't contain it"
          <| fun _ ->
              // Oracle: '+(love pleasure) +was' returns no rows even though
              // both docs contain 'was' — stopwords are never indexed.
              let s = booleanScoresOf corpus "+tutorial +the"
              Expect.equal (s |> Array.filter (fun v -> v > 0.0)) [||] "the required stopword excludes everything"

              let optional = booleanScoresOf corpus "tutorial the"
              Expect.isTrue (optional.[0] > 0.0) "an optional stopword just contributes nothing"

          testCase "boolean groups compose required and excluded terms"
          <| fun _ ->
              let grouped = buildCorpus [ "alpha common"; "beta common"; "alpha beta common"; "common" ]
              let matches query =
                  booleanScoresOf grouped query
                  |> Array.mapi (fun index score -> index + 1, score <> 0.0)
                  |> Array.filter snd
                  |> Array.map fst

              Expect.equal (matches "+(alpha beta) +common") [| 1; 2; 3 |] "a required group accepts either positive member"
              Expect.equal (matches "+(alpha beta) +common -beta") [| 1 |] "an exclusion narrows the required intersection"
              Expect.equal (matches "+(alpha +beta) +common") [| 2; 3 |] "a nested required term constrains its group"
              Expect.equal (matches "-(alpha beta) common") [| 4 |] "an excluded group removes every group match"

          testCase "query expansion ranks the seed docs first and pulls in term-sharing docs"
          <| fun _ ->
              // Oracle: AGAINST ('database' WITH QUERY EXPANSION) returns all
              // six rows ordered 1, 5, 3, 6, 2, 4.
              let s = expansionScoresOf corpus "database"
              Expect.all (List.ofArray s |> List.map (fun v -> v > 0.0)) id "expansion reaches every doc"

              let order =
                  s |> Array.mapi (fun i v -> i + 1, v) |> Array.sortByDescending snd |> Array.map fst

              Expect.equal order.[0] 1 "the direct match ranks first"
              Expect.equal order.[1] 5 "the other direct match second"

          testCase "inverted index updates documents without rebuilding the corpus"
          <| fun _ ->
              let index =
                  buildIndexWith defaultCollation [ 10, "database tutorial"; 20, "security handbook" ]
                  |> addDocument 30 "database comparison"
                  |> addDocument 20 "database security"
                  |> removeDocument 10

              let natural = naturalScores index "database"
              let prefix = booleanScores index "+secur*"

              Expect.equal (natural |> Map.keys |> Set.ofSeq) (set [ 20; 30 ]) "replacement and insert are searchable"
              Expect.equal (prefix |> Map.keys |> Set.ofSeq) (set [ 20 ]) "prefix postings follow replacement"

          testCase "single-term score dictionaries preserve materialized scores"
          <| fun _ ->
              let index =
                  buildIndexWith
                      defaultCollation
                      [ 10, "database tutorial"; 20, "database security"; 30, "security handbook" ]

              let actual =
                  tryNaturalSingleTermScoresDictionary index "database"
                  |> Option.get
                  |> Seq.map (fun (KeyValue(id, score)) -> id, score)
                  |> Map.ofSeq

              Expect.equal actual (naturalScores index "database") "dictionary scores match"
              Expect.isNone (tryNaturalSingleTermScoresDictionary index "database security") "multiple terms use combined scoring"

          testCase "candidate-scoped scoring preserves corpus-wide relevance"
          <| fun _ ->
              let index =
                  buildIndexWith
                      defaultCollation
                      [ 10, "database tutorial"
                        20, "database security"
                        30, "security handbook"
                        40, "unrelated material" ]

              let candidates = set [ 20; 30 ]
              let restricted scores = scores |> Map.filter (fun id _ -> candidates.Contains id)

              Expect.equal
                  (naturalScoresWithin candidates index "database security")
                  (naturalScores index "database security" |> restricted)
                  "natural scoring retains corpus-wide IDF"

              Expect.equal
                  (booleanScoresWithin candidates index "+data*")
                  (booleanScores index "+data*" |> restricted)
                  "boolean prefix scoring retains corpus-wide IDF"

              Expect.equal
                  (expansionScoresWithin candidates index "database")
                  (expansionScores index "database" |> restricted)
                  "query expansion retains its corpus-wide seed pass"

              let dictionary =
                  tryNaturalSingleTermScoresDictionaryWithin (Some candidates) index "database"
                  |> Option.get
                  |> Seq.map (fun (KeyValue(id, score)) -> id, score)
                  |> Map.ofSeq

              Expect.equal dictionary (naturalScores index "database" |> restricted) "dictionary scoring uses the same restriction"

          testCase "flat boolean dictionaries preserve scores and candidate scope"
          <| fun _ ->
              let index =
                  buildIndexWith
                      defaultCollation
                      [ 10, "database concurrency database"
                        20, "database security"
                        30, "concurrency handbook"
                        40, "database concurrency tutorial" ]

              let asMap scores =
                  scores
                  |> Seq.map (fun (KeyValue(id, score)) -> id, score)
                  |> Map.ofSeq

              let expected = booleanScores index "+database +concurrency +database"
              let actual = tryFlatBooleanScoresDictionary index "+database +concurrency +database" |> Option.get |> asMap

              Expect.equal actual expected "required terms retain duplicate-term scoring"

              let candidates = set [ 10; 20; 30 ]
              let restricted = expected |> Map.filter (fun id _ -> candidates.Contains id)

              let scoped =
                  tryFlatBooleanScoresDictionaryWithin (Some candidates) index "+database +concurrency +database"
                  |> Option.get
                  |> asMap

              Expect.equal scoped restricted "candidate scope changes rows, not corpus-wide IDF"

              let optionalExpected =
                  booleanScores index "database concurrency"
                  |> Map.filter (fun id _ -> candidates.Contains id)

              let optionalScoped =
                  tryFlatBooleanScoresDictionaryWithin (Some candidates) index "database concurrency"
                  |> Option.get
                  |> asMap

              Expect.equal optionalScoped optionalExpected "optional terms respect candidate scope"
              Expect.isEmpty
                  (tryFlatBooleanScoresDictionary index "+database +missing" |> Option.get)
                  "a missing required posting makes the result empty"

              let prefixExpected = booleanScores index "+data* +concur*"
              let prefixActual = tryFlatBooleanScoresDictionary index "+data* +concur*" |> Option.get |> asMap

              Expect.equal prefixActual prefixExpected "required prefixes use their maintained postings"
              Expect.isEmpty
                  (tryFlatBooleanScoresDictionary index "+the" |> Option.get)
                  "an unindexed required stopword cannot match"

              for query in
                  [ "database concurrency -security"
                    ">database <concurrency ~tutorial"
                    "-database"
                    "data* concur*" ] do
                  let expected = booleanScores index query
                  let actual = tryFlatBooleanScoresDictionary index query |> Option.get |> asMap
                  Expect.equal actual expected (sprintf "flat scoring matches for %s" query)

              Expect.isNone (tryFlatBooleanScoresDictionary index "\"database concurrency\"") "phrases use general scoring"
              Expect.isNone (tryFlatBooleanScoresDictionary index "(+database +concurrency)") "groups use general scoring"

          testCase "a deeply nested boolean query is bounded, not a stack overflow"
          <| fun _ ->
              // Thousands of open parens must not overflow the recursive
              // parser/evaluator — the depth cap turns excess nesting into
              // ignorable punctuation.
              let deep = String.replicate 5000 "(" + "mysql" + String.replicate 5000 ")"
              let scores = booleanScoresOf corpus deep
              Expect.equal scores.Length articles.Length "returns a score per doc without crashing" ]
