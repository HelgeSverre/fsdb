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
        [ testCase "custom stopwords use the source collation after indexed token normalization"
          <| fun _ ->
              let names = [ "utf8mb4_0900_ai_ci"; "utf8mb4_0900_as_cs"; "utf8mb4_bin" ]
              for source in names do
                  let policy = customStopwords (tryFind source |> Option.get) [ "Orchard"; "café" ]
                  for target in names do
                      let index = buildIndexWithStopwords policy defaultTokenizer (tryFind target |> Option.get)
                                      [ 1, [ "orchard" ]; 2, [ "Orchard" ]; 3, [ "ORCHARD" ]
                                        4, [ "cafe" ]; 5, [ "café" ]; 6, [ "CAFÉ" ]; 7, [ "the" ] ]
                      for query in [ "orchard"; "Orchard"; "ORCHARD" ] do
                          let expected =
                              if source = names.Head then []
                              elif target <> "utf8mb4_bin" then [ 1; 2; 3 ]
                              elif query = "orchard" then [ 1 ]
                              elif query = "ORCHARD" then [ 3 ]
                              else []
                          Expect.equal (naturalScores index query |> Map.keys |> Seq.toList) expected "source collation filters indexed spellings"
                      for query in [ "cafe"; "café"; "CAFÉ" ] do
                          let expected =
                              if source = names.Head then []
                              elif target = names.Head || query = "cafe" then [ 4 ]
                              elif target = "utf8mb4_bin" && query = "CAFÉ" then [ 6 ]
                              else []
                          Expect.equal (naturalScores index query |> Map.keys |> Seq.toList) expected "source accent sensitivity and target lookup remain distinct"
                      Expect.equal (naturalScores index "the" |> Map.keys |> Seq.toList) [ 7 ] "custom lists replace built-in words"

          testCase "word bounds preserve old postings while new writes use current lengths"
          <| fun _ ->
              let broad = Words { Minimum = 1; Maximum = 84 }
              let restricted = Words { Minimum = 3; Maximum = 10 }
              let initial = buildIndexWithStopwords StopwordPolicy.Disabled broad defaultCollation
                                [ 1, [ "xy" ]; 2, [ "abcdefghijk" ]; 3, [ "orchard" ]; 4, [ "zzzz" ] ]
              let changed = initial |> withTokenizer restricted |> addDocument 5 "xy abcdefghijk orchard"
              let restored = changed |> withTokenizer broad
              let ids scores = scores |> Map.keys |> Seq.toList
              for index, longIds, orchardIds in [ initial, [ 2 ], [ 3 ]; changed, [], [ 3; 5 ]; restored, [ 2 ], [ 3; 5 ] ] do
                  for score in [ naturalScores; booleanScores ] do
                      Expect.equal (score index "xy" |> ids) [ 1 ] "exact lookup ignores the current minimum"
                      Expect.equal (score index "abcdefghijk" |> ids) longIds "exact lookup observes the current maximum"
                      Expect.equal (score index "orchard" |> ids) orchardIds "new writes use current indexing bounds"
                  Expect.equal (booleanScores index "xy*" |> ids) [ 1 ] "prefix reaches old short postings"
                  Expect.equal (booleanScores index "abc*" |> ids) [ 2 ] "prefix reaches old long postings"

          testCase "query expansion filters historical seed text using current word bounds"
          <| fun _ ->
              let broad = Words { Minimum = 1; Maximum = 84 }
              let restricted = Words { Minimum = 3; Maximum = 10 }
              let original = buildIndexWithStopwords StopwordPolicy.Disabled broad defaultCollation
                                 [ 1, [ "orchard xy abcdefghijk" ]; 2, [ "xy" ]; 3, [ "abcdefghijk" ]; 4, [ "zzzz" ] ]
              let ids index = expansionScores index "orchard" |> Map.keys |> Seq.toList
              Expect.equal (ids original) [ 1; 2; 3 ] "original seed bounds"
              let changed = original |> withTokenizer restricted
              Expect.equal (ids changed) [ 1 ] "seed extraction honors current bounds"
              Expect.equal (changed |> withTokenizer broad |> ids) [ 1; 2; 3 ] "restoring bounds restores expansion"
              let narrow = buildIndexWithStopwords StopwordPolicy.Disabled restricted defaultCollation
                               [ 1, [ "orchard xy abcdefghijk" ]; 4, [ "zzzz" ] ]
              let widened = narrow |> withTokenizer broad |> addDocument 2 "xy" |> addDocument 3 "abcdefghijk"
              Expect.equal (ids widened) [ 1; 2; 3 ] "newly permitted seed words reach newer postings"

          testCase "custom ngram stopwords match substrings with source collation expansions and padding"
          <| fun _ ->
              let documents = [ 1, [ "ab" ]; 2, [ "Ab" ]; 3, [ "AB" ]; 4, [ "áb" ]; 5, [ "ss" ]; 6, [ "ßx" ]; 7, [ "zz" ] ]
              for source, word, expected in
                  [ "utf8mb4_0900_ai_ci", "A", [ 5; 6; 7 ]
                    "utf8mb4_0900_ai_ci", "ss", [ 1; 2; 3; 4; 7 ]
                    "utf8mb4_0900_ai_ci", "ß", [ 1; 2; 3; 4; 7 ]
                    "utf8mb4_0900_as_cs", "A", [ 1; 2; 3; 4; 5; 6; 7 ]
                    "utf8mb4_bin", "a ", [ 4; 5; 6; 7 ]
                    "utf8mb4_0900_ai_ci", "a ", [ 1; 2; 3; 4; 5; 6; 7 ]
                    "utf8mb4_0900_ai_ci", "abc", [ 1; 2; 3; 4; 5; 6; 7 ] ] do
                  let policy = customStopwords (tryFind source |> Option.get) [ word ]
                  let index = buildIndexWithStopwords policy (Ngrams 2) defaultCollation documents
                  Expect.equal (naturalScores index "ab Ab AB áb ss ßx zz" |> Map.keys |> Seq.toList) expected "substring filtering observes the source collation"

          testCase "ngram writes apply custom stopwords to the first spelling of a posting"
          <| fun _ ->
              let policy = customStopwords (tryFind "utf8mb4_bin" |> Option.get) [ "a " ]
              let documents =
                  [ 1, [ "京a"; "京Ａ" ]
                    2, [ "京Ａ"; "京a" ]
                    3, [ "京Ａ 京Ａ"; "京a" ]
                    4, [ "雑音"; "" ] ]
              let cached = buildIndexWithStopwords policy (Ngrams 2) defaultCollation documents
              let scores = naturalScores cached "京a"
              Expect.equal (scores |> Map.keys |> Seq.toList) [ 2; 3 ] "the first spelling decides whether all positions are retained"
              closeTo (1.5 * scores.[2]) scores.[3] "retained spellings contribute their original frequencies"
              Expect.equal (booleanScores cached "京Ａ" |> Map.keys |> Seq.toList) [ 2; 3 ] "the allowed spelling can search the posting"
              Expect.isEmpty (booleanScores cached "京a") "the blocked query spelling still has no Boolean lookup"
              let rules = indexingRules (Ngrams 2) policy
              let bulk =
                  documents |> Seq.map (fun (id, fields) -> id, rules, fields)
                  |> buildIndexWithDocumentSettings rules defaultCollation
              let scores = naturalScores bulk "京a"
              Expect.equal (scores |> Map.keys |> Seq.toList) [ 1; 2; 3 ] "bulk indexing filters individual spellings"
              closeTo (2.0 * scores.[2]) scores.[3] "bulk postings count only surviving occurrences"
              let rewritten = bulk |> addDocumentFields 1 [ "京a"; "京Ａ" ]
              Expect.equal (naturalScores rewritten "京a" |> Map.keys |> Seq.toList) [ 2; 3 ] "a rewritten row follows the write cache rule"

          testCase "custom word lists replace defaults and control phrase anchors"
          <| fun _ ->
              let documents = [ 1, [ "orchard cobalt" ]; 2, [ "other cobalt" ]; 3, [ "the cobalt" ]; 4, [ "orchard the cobalt" ]; 5, [ "zzzz" ] ]
              let policy = customStopwords defaultCollation [ "orchard" ]
              let index = buildIndexWithStopwords policy defaultTokenizer defaultCollation documents
              for score in [ naturalScores; booleanScores ] do
                  Expect.equal (score index "\"orchard cobalt\"" |> Map.keys |> Seq.toList) [ 1; 2; 3; 4 ] "custom stopwords do not anchor a phrase"
                  Expect.equal (score index "\"orchard the cobalt\"" |> Map.keys |> Seq.toList) [ 3; 4 ] "built-in stopwords become searchable phrase anchors"
              Expect.isEmpty (booleanScores index "+orchard +cobalt") "a required custom stopword has no postings"
              Expect.isEmpty (booleanScores index "orch*") "prefixes exclude filtered documents"
              let empty = customStopwords defaultCollation [ null; "" ]
              let index = buildIndexWithStopwords empty defaultTokenizer defaultCollation documents
              Expect.equal (naturalScores index "the" |> Map.keys |> Seq.toList) [ 3; 4 ] "empty lists disable default filtering"

          testCase "binary exact phrases verify folded text at original anchor positions"
          <| fun _ ->
              let documents =
                  [ 1, "orchard cobalt"; 2, "Orchard Cobalt"; 3, "ORCHARD COBALT"
                    4, "Orchard cobalt orchard"; 5, "orchard Cobalt cobalt"
                    6, "orchard cobalt Orchard Cobalt"; 7, "zzzz" ]
              for name in [ "utf8mb4_bin"; "utf8mb4_0900_bin" ] do
                  let collation = tryFind name |> Option.get
                  for tokenizer in [ defaultTokenizer; Ngrams 2 ] do
                      let rules = indexingRules tokenizer StopwordPolicy.Disabled
                      let index =
                          buildIndexWithDocumentSettings rules collation
                              (documents |> List.map (fun (id, text) -> id, rules, [ text ]))
                      let ids scores = scores |> Map.keys |> Seq.toList
                      Expect.equal (booleanScores index "\"orchard cobalt\"" |> ids) [ 1; 5; 6 ] "verification folds later document tokens but retains original anchor positions"
                      for query in [ "\"Orchard cobalt\""; "\"orchard Cobalt\""; "\"Orchard Cobalt\"" ] do
                          Expect.isEmpty (booleanScores index query) "uppercase query tokens fail multi-token verification"
                      match tokenizer with
                      | Words _ ->
                          Expect.equal (naturalScores index "\"orchard cobalt\"" |> ids) [ 1; 5; 6 ] "natural phrases use the same verifier"
                          Expect.equal (booleanScores index "\"Orchard\"" |> ids) [ 2; 4; 6 ] "a single quoted token keeps binary lookup semantics"
                          Expect.equal (booleanScores index "\"Orchard Cobalt\" @10" |> ids) [ 2; 6 ] "proximity uses original postings"
                          Expect.isEmpty (expansionScores index "\"Orchard Cobalt\"") "failed phrases cannot seed expansion"
                      | Ngrams _ ->
                          Expect.isEmpty (booleanScores index "Orchard") "a multi-gram word uses exact phrase verification"
                          Expect.equal (booleanScores index "\"Or\"" |> ids) [ 2; 4; 6 ] "one gram keeps binary lookup semantics"
                          Expect.equal (naturalScores index "Orchard" |> ids) [ 1; 2; 4; 5; 6 ] "natural ngrams still union original postings"

          testCase "binary phrase verification retains candidate requirements and folds short internal words"
          <| fun _ ->
              let collation = tryFind "utf8mb4_bin" |> Option.get
              let index = buildIndexWith collation
                              [ 1, "orchard Cobalt"; 2, "orchard XX cobalt"
                                3, "orchard café"; 4, "orchard CAFÉ café"; 5, "zzzz" ]
              for score in [ naturalScores; booleanScores ] do
                  Expect.isEmpty (score index "\"orchard cobalt\"") "folding cannot create a missing lowercase posting"
                  Expect.equal (score index "\"orchard xx cobalt\"" |> Map.keys |> Seq.toList) [ 2 ] "unindexed internal words are verified in lowercase"
                  Expect.isEmpty (score index "\"orchard XX cobalt\"") "query tokens retain case"
                  Expect.equal (score index "\"orchard café\"" |> Map.keys |> Seq.toList) [ 3; 4 ] "document folding includes accented letters"
                  Expect.isEmpty (score index "\"orchard CAFÉ\"") "uppercase accented queries fail verification"

          testCase "nonbinary full-text collations fold case for words and ngrams"
          <| fun _ ->
              let collation = tryFind "utf8mb4_0900_as_cs" |> Option.get
              for tokenizer in [ defaultTokenizer; Ngrams 2 ] do
                  let rules = indexingRules tokenizer StopwordPolicy.Disabled
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

          testCase "Chinese 0900 ngram postings preserve tailored equivalence in mixed-script text"
          <| fun _ ->
              let collation = tryFind "utf8mb4_zh_0900_as_cs" |> Option.get
              let rules = indexingRules (Ngrams 2) StopwordPolicy.BuiltIn
              let index =
                  [ "文ſ"; "文ꞅ"; "文s"; "文國"; "文国"; "文乾"; "文亁" ]
                  |> List.mapi (fun i body -> i + 1, rules, [ body ])
                  |> buildIndexWithDocumentSettings rules collation

              let ids scores = scores |> Map.keys |> Seq.toList
              for query, expected in [ "文ſ", [ 1; 2 ]; "文ꞅ", [ 1; 2 ]; "文s", [ 3 ]; "文国", [ 5 ]; "文乾", [ 6 ] ] do
                  Expect.equal (naturalScores index query |> ids) expected query

              Expect.equal (booleanScores index "\"文ſ\"" |> ids) [ 1; 2 ] "the quoted query shares the tailored posting"
              Expect.equal (expansionScores index "文ſ" |> ids) [ 1; 2 ] "expansion stays within the matched equivalence class"

          testCase "Boolean ngram boundaries follow the collation's lowercase table"
          <| fun _ ->
              let rules = indexingRules (Ngrams 2) StopwordPolicy.BuiltIn
              let documents =
                  [ "中Ɥ"; "中ɥ"; "中ẞ"; "中ß"; "中Ꞅ"; "中ꞅ"; "中Ɑ"; "中ɑ"; "中h" ]
                  |> List.mapi (fun i body -> i + 1, rules, [ body ])

              let ids index query = booleanScores index query |> Map.keys |> Seq.toList
              for name, turnedH, sharpS, alpha in
                  [ "utf8mb4_zh_0900_as_cs", [ 1; 2 ], [ 3; 4 ], [ 7; 8 ]
                    "utf8mb4_ja_0900_as_cs_ks", [ 1; 2 ], [ 3; 4 ], [ 7; 8 ]
                    "utf8mb4_unicode_520_ci", [], [ 3; 4 ], [ 7; 8 ]
                    "utf8mb4_unicode_ci", [], [], []
                    "utf8mb4_0900_bin", [], [], [] ] do
                  let index = buildIndexWithDocumentSettings rules (tryFind name |> Option.get) documents
                  for term, expected in [ "中Ɥ", turnedH; "中ẞ", sharpS; "中Ɑ", alpha; "中Ꞅ", [] ] do
                      let phrase = "\"" + term + "\""
                      Expect.equal (ids index term) expected (name + " bare " + term)
                      Expect.equal (ids index phrase) expected (name + " quoted " + term)

              let chinese = tryFind "utf8mb4_zh_0900_as_cs" |> Option.get
              let cherokee =
                  buildIndexWithDocumentSettings rules chinese
                      [ 1, rules, [ "中文Ꭰ" ]; 2, rules, [ "中文ꭰ" ]; 3, rules, [ "中文Ɥ" ]; 4, rules, [ "中文ɥ" ] ]
              Expect.equal (ids cherokee "\"中文Ꭰ\"") [ 1; 2; 3; 4 ] "the folded Cherokee capital ends the word"

          testCase "legacy ngram postings respect the collation's lowercase mappings"
          <| fun _ ->
              let rules = indexingRules (Ngrams 2) StopwordPolicy.BuiltIn
              let documents =
                  [ "中Ɥ"; "中ɥ"; "中Ꭰ"; "中ꭰ"; "中ẞ"; "中ß"; "日本語" ]
                  |> List.mapi (fun i body -> i + 1, rules, [ body ])
              let ids scores = scores |> Map.keys |> Seq.toList

              for name in [ "utf8mb4_general_ci"; "utf8mb4_unicode_ci"; "utf8mb4_unicode_520_ci" ] do
                  let index = buildIndexWithDocumentSettings rules (tryFind name |> Option.get) documents
                  let capitalSharpS, smallSharpS =
                      if name = "utf8mb4_unicode_520_ci" then [ 5; 6 ], [ 5; 6 ] else [ 5 ], [ 6 ]

                  for query, expected in
                      [ "中Ɥ", [ 1 ]; "中ɥ", [ 2 ]; "中Ꭰ", [ 3 ]; "中ꭰ", [ 4 ]; "中ẞ", capitalSharpS; "中ß", smallSharpS ] do
                      Expect.equal (naturalScores index query |> ids) expected (name + " natural " + query)
                      Expect.equal (expansionScores index query |> ids) expected (name + " expansion " + query)

                  for query, expected in [ "中ɥ", [ 2 ]; "中Ꭰ", [ 3 ]; "中ß", smallSharpS ] do
                      Expect.equal (booleanScores index ("\"" + query + "\"") |> ids) expected (name + " Boolean " + query)

          testCase "legacy general ngram postings give dotted and dotless I the same weight"
          <| fun _ ->
              let documents =
                  [ 1, [ "京İ花"; "京ı花" ]; 2, [ "雑音" ]; 3, [ "東京" ]; 4, [ "京Ｉ花" ] ]

              for name in [ "utf8mb3_general_ci"; "utf8mb4_general_ci" ] do
                  let index =
                      buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) (tryFind name |> Option.get) documents

                  for view in [ index; flushPostings index ] do
                      for term, row, score in
                          [ "京i", 1, 0.7249524593353271; "京I", 1, 0.7249524593353271
                            "京İ", 1, 0.7249524593353271; "京ı", 1, 0.7249524593353271
                            "京Ｉ", 4, 0.3624762296676636 ] do
                          for scores in [ naturalScores view term; booleanScores view ("\"" + term + "\"") ] do
                              let label = name + " " + term
                              Expect.equal (scores |> Map.keys |> Seq.toList) [ row ] label
                              closeTo score scores.[row] label

          testCase "recovery syncs cached ngram streams whose collation keys have merged"
          <| fun _ ->
              let collation = tryFind "utf8mb4_general_ci" |> Option.get
              let index =
                  buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) collation []
                  |> flushPostings
                  |> addDocument 1 "京İ"
                  |> addDocument 2 "京ı"
                  |> removeDocument 2
              let next = nextDocumentOrder index
              let through = flushedThrough index
              let spellings = Some [ "京i"; "京ı" ]
              let recovered = restoreCache (Some next) through spellings None false index

              Expect.equal (flushedThrough recovered) (Some(next - 1L)) "the merged postings are synced"
              Expect.isEmpty (cachedTermsInInsertionOrder recovered) "the old cache has no pending terms"
              Expect.isNonEmpty (deletedPostings index) "a cached delete is present"
              Expect.equal (deletedPostings recovered) (deletedPostings index) "the cached delete is retained"
              Expect.equal (naturalScores recovered "京ı") (naturalScores index "京i") "ranking uses the merged postings"

          testCase "legacy utf8mb3 ngram postings fold Kana and keep Hangul syllables distinct from Jamo"
          <| fun _ ->
              let rules = indexingRules (Ngrams 2) StopwordPolicy.BuiltIn
              let documents =
                  [ "か"; "ガ"; "ｶ"; "ゕ"; "が"; "ｶﾞ"; "하"; "하"; "ᄒ" ]
                  |> List.mapi (fun i text -> i + 1, rules, [ text + "中"; "日" + text ])
              let collation = tryFind "utf8mb3_unicode_ci" |> Option.get
              let index = buildIndexWithDocumentSettings rules collation documents
              for view in [ index; flushPostings index ] do
                  let ids scores = scores |> Map.keys |> Seq.toList
                  Expect.equal (naturalScores view "ガ中" |> ids) [ 1; 2; 3; 4 ] "exact gram matches Kana width, script, size, and voice"
                  Expect.equal (booleanScores view "ｶ*" |> ids) [ 1; 2; 3; 4; 5; 6 ] "prefix also reaches decomposed voicing"
                  Expect.equal (booleanScores view "하*" |> ids) [ 7 ] "Hangul syllable does not compose from Jamo postings"
                  Expect.equal (booleanScores view "ᄒ*" |> ids) [ 8; 9 ] "leading Jamo still matches the decomposed spelling"

          testCase "ngram Kana ligatures share bidirectional wildcard keys in Unicode collations"
          <| fun _ ->
              let documents = [ 1, "ヿ中"; 2, "こと中"; 3, "ゟ中"; 4, "より中"; 5, "花束" ]
              for name in [ "utf8mb3_unicode_ci"; "utf8mb4_unicode_ci"; "utf8mb4_unicode_520_ci"; "utf8mb4_0900_ai_ci" ] do
                  let index = buildIndexWithTokenizer (Ngrams 2) (tryFind name |> Option.get) documents
                  for term, expected in [ "ヿ*", [ 1; 2 ]; "こ*", [ 1; 2 ]; "ゟ*", [ 3; 4 ]; "よ*", [ 3; 4 ] ] do
                      let ids = booleanScores index term |> Map.keys |> Seq.toList
                      Expect.equal ids expected (name + " " + term)

          testCase "accent-sensitive ngram prefixes can end inside CJK expansions"
          <| fun _ ->
              let documents =
                  [ 1, "㍿中"; 2, "株中"; 3, "㌔中"; 4, "キ中"; 5, "ヿ中"; 6, "こと中"
                    7, "ゟ中"; 8, "より中"; 9, "한中"; 10, "하中"; 11, "ᄒ中"; 12, "花束"; 13, "㌐中" ]
              let collation = tryFind "utf8mb4_0900_as_ci" |> Option.get
              let index = buildIndexWithTokenizer (Ngrams 2) collation documents
              Expect.isEmpty (booleanScores index "ギ*") "cached prefixes do not match a synthetic voiced spelling"
              for view in [ index; flushPostings index ] do
                  for term, expected in
                      [ "株*", [ 1; 2 ]; "キ*", [ 3; 4; 13 ]; "こ*", [ 5; 6 ]; "ヿ*", [ 5; 6 ]
                        "よ*", [ 7; 8 ]; "ゟ*", [ 7; 8 ]; "ᄒ*", [ 9; 10; 11 ]; "하*", [ 9; 10 ] ] do
                      let ids = booleanScores view term |> Map.keys |> Seq.toList
                      Expect.equal ids expected term

          testCase "accent-sensitive cached ngram prefixes stop at a nonmatching neighbor"
          <| fun _ ->
              let collation = tryFind "utf8mb4_0900_as_ci" |> Option.get
              let rules = indexingRules (Ngrams 2) StopwordPolicy.Disabled
              let cache texts =
                  texts
                  |> List.mapi (fun i text -> i + 1, rules, [ text ])
                  |> buildIndexWithDocumentSettings rules collation
              let ids index term = booleanScores index term |> Map.keys |> Seq.toList

              Expect.equal (ids (cache [ "e中"; "é中"; "é中" ]) "é*") [ 2 ] "the plain posting separates the two accented spellings"
              Expect.equal (ids (cache [ "é中"; "é中" ]) "é*") [ 1; 2 ] "both spellings match without the intervening posting"
              Expect.equal (ids (cache [ "㌐中" ]) "ギ*") [ 1 ] "a voiced compatibility prefix can match in the cache"
              Expect.equal (ids (cache [ "ā中"; "Å中"; "Å中" ]) "Å*") [ 2; 3 ] "the search compares the neighbor's complete accent"
              let rows = tryFlatBooleanScoresDictionary (cache [ "e中"; "é中"; "é中" ]) "é*" |> Option.get
              Expect.equal (rows.Keys |> Seq.toList) [ 2 ] "the flat posting lookup uses the same boundary"

          testCase "accent-sensitive ngram prefixes seek flushed keys before merging cached matches"
          <| fun _ ->
              let collation = tryFind "utf8mb4_0900_as_ci" |> Option.get
              let build = buildIndexWithTokenizer (Ngrams 2) collation
              let accents = build [ 1, "e中"; 2, "é中"; 3, "é中" ] |> flushPostings
              let mixed = accents |> addDocument 101 "e中" |> addDocument 102 "é中" |> addDocument 103 "é中"
              let kana = build [ 1, "㌐中"; 2, "キ中"; 3, "ギ中"; 4, "㌔中"; 5, "ギ中"; 6, "き中" ] |> flushPostings

              for index, query, expected in [ accents, "é*", [ 3 ]; mixed, "é*", [ 3; 102 ]; kana, "ギ*", [ 1; 5 ] ] do
                  let scores = booleanScores index query
                  Expect.equal (scores.Keys |> Seq.toList) expected query
                  let flat = tryFlatBooleanScoresDictionary index query |> Option.get
                  Expect.equal (flat.Keys |> Seq.sort |> Seq.toList) expected "the flat lookup uses the same boundary"
                  for id in expected do
                      closeTo 0.227644696831703 scores.[id] "matching postings share DF across cache and disk"
                      closeTo scores.[id] flat.[id] "flat and general evaluators agree"

          testCase "accent-sensitive ngram cache inserts grams in MySQL collation order"
          <| fun _ ->
              let collation = tryFind "utf8mb4_0900_as_ci" |> Option.get
              let rules = indexingRules (Ngrams 2) StopwordPolicy.Disabled
              let index =
                  [ "中"; "花"; "株"; "㍿"; "ｷﾞ"; "ǽ"; "㋿" ]
                  |> List.mapi (fun i word -> i + 1, rules, [ word + "中 " + word + "花"; "田" + word + " 上海" ])
                  |> buildIndexWithDocumentSettings rules collation

              Expect.isEmpty (booleanScores index "花*") "the leading combining mark sends the cache search away from 花中"
              let flat = tryFlatBooleanScoresDictionary index "花*" |> Option.get
              Expect.isEmpty flat "the SQL fast path has the same cache traversal"
              Expect.isEmpty (booleanScores (flushPostings index) "花*") "the flushed cursor stops at the combining mark"

          testCase "Unicode 5.2 ngrams share padded space keys but retain Mongolian separator weight"
          <| fun _ ->
              let collation = tryFind "utf8mb4_unicode_520_ci" |> Option.get
              let rules = indexingRules (Ngrams 2) StopwordPolicy.BuiltIn
              let characters = [ "\u3000"; "\u00a0"; "\u200c"; "\u200d"; "\u2060"; "\u180e"; " " ]
              let documents =
                  characters
                  |> List.mapi (fun index character -> index + 1, rules, [ "生" + character + "日"; "" ])
              let index = buildIndexWithDocumentSettings rules collation documents
              let ids scores = scores |> Map.keys |> Seq.toList
              let spaceScores = naturalScores index "生\u3000日"
              Expect.equal (ids spaceScores) [ 1; 2; 3; 4; 5 ] "space and ignorable-form ngrams share one posting"
              Expect.isGreaterThan spaceScores.[1] spaceScores.[3] "both matching space ngrams outrank one shared posting"
              Expect.equal (ids (naturalScores index "生\u180e日")) [ 6 ] "weighted separator has distinct ngrams"
              Expect.equal (ids (booleanScores index "生\u3000日")) [ 1; 2; 3; 4; 5 ] "Boolean lookup uses the same padded keys"

          testCase "UCA 9 accent-insensitive size-one ngrams share ignorable mark postings"
          <| fun _ ->
              let collation = tryFind "utf8mb4_0900_ai_ci" |> Option.get
              let rules = indexingRules (Ngrams 1) StopwordPolicy.BuiltIn
              let index =
                  buildIndexWithDocumentSettings rules collation
                      [ 1, rules, [ "生\u180e日"; "蒙古分隔" ]
                        2, rules, [ "生日"; "" ]
                        3, rules, [ "ｶﾞｯｺｳ"; "" ]
                        4, rules, [ "ガッコウ"; "" ] ]
              let ids scores = scores |> Map.keys |> Seq.toList
              Expect.equal (ids (naturalScores index "ｶﾞｯｺｳ")) [ 1; 3; 4 ] "halfwidth mark reaches the separator posting"
              Expect.equal (ids (naturalScores index "生\u180e日")) [ 1; 2; 3 ] "separator query reaches the halfwidth mark posting"

          testCase "older Unicode Boolean ngrams do not compose Korean Jamo into short terms"
          <| fun _ ->
              let rules = indexingRules (Ngrams 5) StopwordPolicy.BuiltIn
              for name in [ "utf8mb4_unicode_ci"; "utf8mb4_unicode_520_ci"; "utf8mb4_general_ci" ] do
                  let collation = tryFind name |> Option.get
                  let index = buildIndexWithDocumentSettings rules collation
                                  [ 1, rules, [ "하늘하늘"; "한글" ] ]
                  Expect.isEmpty (booleanScores index "하늘") (name + " short composed query does not address a Jamo ngram")

          testCase "UCA 9 Boolean ngrams compose Korean Jamo into short terms"
          <| fun _ ->
              let rules = indexingRules (Ngrams 5) StopwordPolicy.BuiltIn
              let collation = tryFind "utf8mb4_0900_ai_ci" |> Option.get
              let index = buildIndexWithDocumentSettings rules collation [ 1, rules, [ "하늘"; "" ] ]
              Expect.equal (booleanScores index "하늘" |> Map.keys |> Seq.toList) [ 1 ] "short composed query reaches the Jamo posting"

          testCase "Boolean ngram queries split words at ASCII apostrophes"
          <| fun _ ->
              let documents =
                  [ 1, [ "東京" ]; 2, [ "東京駅" ]; 3, [ "東京'駅" ]; 4, [ "京駅" ]
                    5, [ "北京" ]; 6, [ "飯店" ]; 7, [ "北京飯店" ]; 8, [ "北京'飯店" ]
                    9, [ "北京 飯店" ]; 10, [ "하늘'나라" ]; 11, [ "하늘 나라" ]; 12, [ "하늘나라" ] ]

              for name in [ "utf8mb4_bin"; "utf8mb4_0900_ai_ci"; "utf8mb4_ja_0900_as_cs" ] do
                  let index = buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) (tryFind name |> Option.get) documents
                  for query, expected in
                      [ "\"東京'駅\"", []; "\"東'京駅\"", []; "+東'京駅", []
                        "北京'飯店", [ 5; 6; 7; 8; 9 ]; "+北京'飯店", [ 5; 7; 8; 9 ]
                        "\"北京'飯店\"", [ 8; 9 ]; "\"하늘'나라\"", [ 10; 11 ] ] do
                      Expect.equal (booleanScores index query |> Map.keys |> Seq.toList) expected $"{name} {query}"

              let mixedCase =
                  [ 1, [ "ABC" ]; 2, [ "DEF" ]; 3, [ "ABC'DEF" ]; 4, [ "東京ABC'DEF" ]
                    5, [ "abc def" ]; 6, [ "北京" ] ]
              for name in [ "utf8mb4_bin"; "utf8mb4_0900_bin" ] do
                  let index = buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) (tryFind name |> Option.get) mixedCase
                  let ids query = booleanScores index query |> Map.keys |> Seq.toList
                  for query in [ "ABC'DEF"; "+ABC'DEF"; "ABC'DEF*" ] do
                      Expect.isEmpty (ids query) $"{name} verifies each uppercase word as a multi-gram phrase"
                  Expect.equal (ids "\"AB\"") [ 1; 3; 4 ] $"{name} retains the uppercase gram in the index"
                  Expect.equal (ids "abc'def") [ 5 ] $"{name} matches the lowercase phrases"

          testCase "full-text query boundaries at embedded NUL follow the indexed collation"
          <| fun _ ->
              let documents =
                  [ 1, "生日"; 2, "快乐"; 3, "生日快乐"; 4, "生日\u0000快乐"; 5, "大阪" ]
              let build collation =
                  buildIndexWithTokenizer (Ngrams 2) (tryFind collation |> Option.get) documents
              let query = "生日\u0000快乐"
              for collation in [ "utf8mb4_0900_ai_ci"; "utf8mb4_unicode_520_ci"; "utf8mb3_unicode_ci" ] do
                  let index = build collation
                  for scores in [ naturalScores index; booleanScores index; expansionScores index ] do
                      Expect.isNonEmpty (scores "生日") "the first term is indexed"
                      Expect.equal (scores query) (scores "生日") collation
                      Expect.isEmpty (scores "\u0000生日") "leading NUL leaves no search terms"
                  let fastScores text =
                      tryNaturalSingleTermScoresDictionary index text
                      |> Option.map (fun scores -> scores |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq)
                  Expect.equal (fastScores query) (Some(naturalScores index "生日")) "single-term scoring trims the same suffix"
                  Expect.equal (booleanScores index "\"生日\u0000快乐\"") (booleanScores index "\"生日") "trim before reading the quote"
                  Expect.equal (booleanScores index "生日\u0000)") (booleanScores index "生日") "trim before checking the group"
                  Expect.isTrue ((naturalScores index "快乐").ContainsKey 4) "both sides of NUL remain indexed in the document"
              for collation in [ "utf8mb4_bin"; "utf8mb4_0900_bin"; "utf8mb4_turkish_ci" ] do
                  let index = build collation
                  for scores in [ naturalScores index; booleanScores index; expansionScores index ] do
                      Expect.equal (scores query) (scores "生日 快乐") collation
                  Expect.equal (booleanScores index "\"生日\u0000快乐\"" |> Map.keys |> Seq.toList) [ 4 ] "quoted terms still search both sides"

              let words = buildIndexWithTokenizer defaultTokenizer defaultCollation [ 1, "生日会"; 2, "庆祝会"; 3, "大阪府" ]
              Expect.equal (naturalScores words "生日会\u0000庆祝会") (naturalScores words "生日会") "word queries use the same input boundary"
              Expect.isNone (booleanSyntaxErrorInView (readView words) "生日会\u0000)") "the discarded suffix cannot introduce a syntax error"

          testCase "Boolean ngram modifiers follow ASCII space boundaries"
          <| fun _ ->
              let collation = tryFind "utf8mb4_bin" |> Option.get
              let documents = [ 1, [ "生日" ]; 2, [ "快乐" ]; 3, [ "生日快乐" ]; 4, [ "東京" ] ]
              let index = buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) collation documents
              let scores = booleanScores index
              for query, equivalent in
                  [ "生日+快乐", "生日 快乐"
                    "生日-快乐", "生日 快乐"
                    "生日>快乐", "生日 快乐"
                    "生日~快乐", "生日 快乐"
                    "生日\t+快乐", "生日 快乐"
                    "生日，-快乐", "生日 快乐"
                    "生日 + 快乐", "生日 快乐"
                    "生日 ++快乐", "生日 +快乐"
                    "生日 +-快乐", "生日 -快乐"
                    "生日 -+快乐", "生日 +快乐"
                    "生日 ><快乐", "生日 快乐"
                    "生日 ~~快乐", "生日 快乐"
                    "生日+(快乐)", "生日 (快乐)"
                    "+\"生日\"-快乐", "+\"生日\" 快乐" ] do
                  Expect.equal (scores query) (scores equivalent) $"{query} has the same membership and rank as {equivalent}"
              Expect.equal (scores "生日 +快乐" |> Map.keys |> Seq.toList) [ 2; 3 ] "a modifier after ASCII space remains active"
              Expect.equal (scores "生日 -快乐" |> Map.keys |> Seq.toList) [ 1 ] "a separated exclusion remains active"

          testCase "Boolean ngram ranking drops rows whose weights cancel to zero"
          <| fun _ ->
              let collation = tryFind "utf8mb4_bin" |> Option.get
              let documents = [ 1, [ "快乐"; "中文" ]; 2, [ "快乐"; "快乐" ] ] @ [ for id in 3..20 -> id, [ "中文" ] ]
              let index = buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) collation documents
              Expect.equal (booleanScores index "快乐") (Map.ofList [ 1, 1.0; 2, 2.0 ]) "unweighted term frequencies"
              for query in [ "<快乐"; "<\"快乐\""; "<(快乐)"; "<快*" ] do
                  Expect.equal (booleanScores index query) (Map.ofList [ 2, 1.0 ]) query
                  Expect.isEmpty (booleanScoresWithin (Set.singleton 1) index query) "restriction keeps the corpus IDF"
                  let scores = tryFlatBooleanScoresDictionary index query |> Option.get
                  Expect.equal (Seq.toList scores.Keys) [ 2 ] "dictionary candidates exclude the zero score"

          testCase "Boolean ngram ranking rounds in matched-term order"
          <| fun _ ->
              let collation = tryFind "utf8mb4_bin" |> Option.get
              let documents =
                  [ yield 1, [ "京都駅 東京駅" ]
                    for id in 2..5 -> id, [ "東京駅" ]
                    for id in 6..10 -> id, [ "大阪府" ] ]
              let index = buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 3) collation documents
              let expected = [ for id in 2..5 -> id, -1.0 ] |> Map.ofList
              for query in [ "+(京* 東京*) <東京*"; "<東京* +(京* 東京*)" ] do
                  Expect.equal (booleanScores index query) expected query
                  Expect.isEmpty (booleanScoresWithin (Set.singleton 1) index query) "a restricted lookup also drops the zero rank"

              let exact = booleanScores index "<東京* 東京*"
              Expect.equal exact (Map.add 1 -1.0 expected) "adding the tiny contribution to the penalty rounds back to -1"
              let scores = tryFlatBooleanScoresDictionary index "<東京* 東京*" |> Option.get
              Expect.equal scores.[1] -1.0 "the dictionary path uses the same precision"
              let tinyRank = 1.885928302414186e-9
              Expect.equal (booleanScores index "東京* 東京*").[1] tinyRank "the contribution stays nonzero without the penalty"
              let reordered = Map.add 1 tinyRank expected
              Expect.equal (booleanScores index "京* <東京* +(東京*)") reordered "the Kyoto contribution can cancel the penalty before Tokyo contributes"
              Expect.equal (booleanScoresWithin (Set.singleton 1) index "京* <東京* +(東京*)") (Map.ofList [ 1, tinyRank ]) "a restricted lookup preserves term order"

          testCase "required ngram prefixes keep their posting bounds across groups"
          <| fun _ ->
              let collation = tryFind "utf8mb4_bin" |> Option.get
              let build documents =
                  documents
                  |> List.mapi (fun index text -> index + 1, [ text ])
                  |> buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) collation
              let ids = Map.keys >> Seq.toList
              let outside = build [ "快乐"; "京都"; "서울역"; "大阪" ]
              let surrounding = build [ "서울역"; "快乐"; "京都"; "서울역" ]
              let inside = build [ "快乐"; "서울역"; "快乐"; "京都" ]
              for query in [ "+서울역 (+快乐 +京*)"; "+서울역 ((+快乐 +京*))" ] do
                  Expect.isEmpty (booleanScores outside query) "the required phrase's postings lie beyond the retained bounds"
                  Expect.equal (booleanScores surrounding query |> ids) [ 1; 4 ] "the entire overlapping posting remains available"
                  Expect.equal (booleanScores inside query |> ids) [ 2 ] "a posting inside the bounds remains available"
                  Expect.isEmpty (booleanScoresWithin (Set.singleton 3) outside query) "outer candidates retain the same group bounds"
                  Expect.equal (booleanScoresWithin (Set.singleton 4) surrounding query |> ids) [ 4 ] "candidate narrowing follows the posting check"
              for query in [ "서울역 (+快乐 +京*)"; "+서울역 (+京* +快乐)"; "+서울역 (+快乐 +京都)" ] do
                  Expect.equal (booleanScores outside query |> ids) [ 3 ] query

              let query = "+서울역 (+快乐 +京*)"
              let original = build [ "서울역"; "서울역"; "快乐"; "京都" ]
              Expect.isEmpty (booleanScores original query) "the original phrase posting is disjoint"
              let updated = addDocument 1 "서울역 서울역" original
              Expect.equal (booleanScores updated query |> ids) [ 1; 2 ] "the updated document extends the posting across the bounds"

          testCase "ngram cache charges posting list growth and segment rollover"
          <| fun _ ->
              let collation = tryFind "utf8mb4_bin" |> Option.get
              let index = buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) collation [] |> flushPostings
              let dense = String.replicate 2048 "日"
              let one = addDocument 1 dense index
              let firstSegment = (one, [ 2..32 ]) ||> List.fold (fun current id -> addDocument id dense current)
              let secondSegment = addDocument 33 dense firstSegment
              Expect.equal (cacheBudget one).Bytes 2872L "one dense document"
              Expect.equal (cacheBudget firstSegment).Bytes 89830L "posting list reallocations"
              Expect.equal (cacheBudget secondSegment).Bytes 92360L "new segment after the 64 KiB boundary"
              Expect.equal (secondSegment |> removeDocument 33 |> cacheBudget).Bytes 92360L "deletion retains charged postings"
              Expect.equal (secondSegment |> syncCachedPostings |> cacheBudget).Bytes 0L "sync clears the cache charge"

          testCase "ngram relevance retains deleted gram frequencies until optimize"
          <| fun _ ->
              let collation = tryFind "utf8mb4_bin" |> Option.get
              let fresh = buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) collation [] |> flushPostings
              let removed =
                  (fresh, [ 1, "東京"; 2, "東海"; 3, "大阪"; 4, "札幌"; 99, "東京 東北" ])
                  ||> List.fold (fun index (id, text) -> addDocument id text index)
                  |> removeDocument 99
              let assertScore (index: Index<int>) query ids expected =
                  let scores = booleanScores index query
                  Expect.equal (scores |> Map.keys |> Seq.toList) ids query
                  closeTo expected scores.[1] query
              assertScore removed "東京" [ 1 ] 0.0906190574169159
              assertScore removed "東*" [ 1; 2 ] 0.015609688125550747
              let dictionary = tryFlatBooleanScoresDictionary removed "東*" |> Option.get
              closeTo 0.015609688125550747 dictionary.[1] "a bare prefix subtracts a deleted document once"
              assertScore removed "+東*" [ 1; 2 ] 1.885928302414186e-9
              assertScore removed "東* 東*" [ 1; 2 ] 0.0906190574169159
              let flushed = flushPostings removed
              assertScore flushed "東京" [ 1 ] 0.3624762296676636
              assertScore flushed "+東*" [ 1; 2 ] 0.0906190574169159

          testCase "ngram natural relevance counts deleted postings in compound and expanded searches"
          <| fun _ ->
              let collation = tryFind "utf8mb4_bin" |> Option.get
              let index =
                  buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) collation
                      [ 1, [ "東京" ]; 2, [ "東海" ]; 3, [ "大阪" ]; 4, [ "札幌" ] ]
                  |> flushPostings
                  |> addDocument 99 "東京 東北"
                  |> removeDocument 99
              let check scores expected label =
                  Expect.equal (scores |> Map.keys |> Seq.toList) [ 1 ] label
                  closeTo expected scores.[1] label

              check (naturalScores index "東京") 0.3624762296676636 "single natural term"
              check (naturalScores index "東京 x") 0.3624762296676636 "short token leaves a single gram"
              check (naturalScores index "東京 東北") 0.0906190574169159 "compound natural query"
              check (naturalScores index "東京 東京") 1.885928302414186e-9 "repeated natural term"
              check (naturalScoresWithin (Set.singleton 1) index "東京 東北") 0.0906190574169159 "candidate filter keeps the full frequency"
              check (expansionScores index "東京") 0.0906190574169159 "one-term expansion"
              check (expansionScores index "東京 東京") 1.885928302414186e-9 "repeated expanded term"
              check (expansionScores (flushPostings index) "東京") 0.3624762296676636 "optimize purges the deleted frequency"
              Expect.isEmpty (expansionScores index "東北") "a deleted-only gram cannot seed expansion"

          testCase "a required ngram prefix bounds a later required gram"
          <| fun _ ->
              let collation = tryFind "utf8mb4_bin" |> Option.get
              let index =
                  buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) collation
                      [ 1, [ "京都 東京" ]; 2, [ "東京" ]; 3, [ "東海" ]; 4, [ "大阪" ] ]
                  |> flushPostings
                  |> addDocument 99 "東京 東北"
                  |> removeDocument 99
              let check query expected =
                  let scores = booleanScores index query
                  Expect.equal (scores |> Map.keys |> Seq.toList) [ 1 ] query
                  closeTo expected scores.[1] query
              check "+京都 +東* +東京" 0.4624868333339691
              check "+京都 +東* 東京" 0.38747745752334595
              check "+京都 +東* +(東京)" 0.38747745752334595

          testCase "required ngram phrases count cached and flushed postings separately"
          <| fun _ ->
              let collation = tryFind "utf8mb4_bin" |> Option.get
              let build flushed cached =
                  let disk =
                      flushed
                      |> List.mapi (fun offset text -> offset + 1, [ text ])
                      |> buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) collation
                      |> flushPostings
                  (disk, cached)
                  ||> List.fold (fun index (id, text) -> addDocument id text index)
              let query = "学校 (+京都 +生* +学校)"
              let prefixGroup = "+学校 (+京都 +生*)"
              let fresh = build [] [ 1, "京都"; 2, "生日"; 3, "学校"; 4, "大阪" ]
              closeTo 0.3624762296676636 (booleanScores fresh query).[3] "disjoint cached postings contribute no frequency"
              closeTo 0.0906190574169159 (booleanScores (flushPostings fresh) query).[3] "disjoint flushed postings still contribute frequency"
              let separate = build [ "学校"; "京都"; "生日"; "大阪" ] [ 5, "学校"; 6, "札幌" ]
              closeTo 0.0906190574169159 (booleanScores separate query).[5] "only the flushed posting counts on the second visit"
              Expect.isEmpty (booleanScores separate prefixGroup) "two disjoint segments do not make one overlapping posting"
              let diskOverlap = build [ "学校"; "京都"; "学校"; "生日"; "大阪" ] [ 6, "学校" ]
              closeTo 0.006269669625908136 (booleanScores diskOverlap query).[1] "the cache segment is disjoint"
              Expect.equal (booleanScores diskOverlap prefixGroup |> Map.keys |> Seq.toList) [ 1; 3 ] "the overlapping disk segment remains available in full"
              let cacheOverlap = build [ "学校"; "大阪" ] [ 3, "学校"; 4, "京都"; 5, "学校"; 6, "生日" ]
              Expect.equal (booleanScores cacheOverlap prefixGroup |> Map.keys |> Seq.toList) [ 3; 5 ] "the overlapping cache segment remains available in full"
              let multi = build [ "学校园"; "京都"; "学校"; "生日"; "大阪" ] [ 6, "学校园" ]
              Expect.isEmpty (booleanScores multi "+学校园 (+京都 +生*)") "one disjoint gram makes the whole phrase unavailable"
              closeTo 0.09688872843980789 (booleanScores multi "学校园 (+京都 +生* +学校园)").[1] "the visitor counts disk postings through the first unavailable gram"

          testCase "general Unicode ngrams retain halfwidth voicing marks in relevance"
          <| fun _ ->
              let rules = indexingRules (Ngrams 6) StopwordPolicy.BuiltIn
              let collation = tryFind "utf8mb4_general_ci" |> Option.get
              let index =
                  buildIndexWithDocumentSettings rules collation
                      [ 1, rules, [ "ﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟ" ]
                        2, rules, [ "日本語日本語" ]
                        3, rules, [ "中文中文" ]
                        4, rules, [ "生日快乐生日快乐" ] ]
              for scores in
                  [ naturalScores index "ﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟ"
                    booleanScores index "ﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟ"
                    expansionScores index "ﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟ" ] do
                  Expect.equal (scores |> Map.keys |> Seq.toList) [ 1 ] "only the matching document scores"
                  closeTo 1.8123811483383179 scores.[1] "MySQL weights five distinct size-6 grams"

          testCase "general Unicode size-5 ngrams separate format and space controls"
          <| fun _ ->
              let rules = indexingRules (Ngrams 5) StopwordPolicy.BuiltIn
              let collation = tryFind "utf8mb4_general_ci" |> Option.get
              let index =
                  buildIndexWithDocumentSettings rules collation
                      [ 1, rules, [ "生日\u200d快乐" ]
                        2, rules, [ "生日\ufe0f快乐" ]
                        3, rules, [ "生日\u00a0快乐" ]
                        4, rules, [ "生日\u3000快乐" ]
                        5, rules, [ "日本語日本語" ]
                        6, rules, [ "中文中文中文" ]
                        7, rules, [ "北海道東京" ]
                        8, rules, [ "韓国語学校" ] ]
              for term, expectedId in [ "生日\u200d快乐", 1; "生日\ufe0f快乐", 2; "生日\u00a0快乐", 3 ] do
                  let ids scores = scores |> Map.keys |> Seq.toList
                  Expect.equal (ids (naturalScores index term)) [ expectedId ] "natural mode keeps the format weight"
                  Expect.equal (ids (expansionScores index term)) [ expectedId ] "query expansion keeps the format weight"
                  let booleanIds = if expectedId = 2 then [ 2 ] else []
                  Expect.equal (ids (booleanScores index term)) booleanIds "Boolean parsing follows MySQL's control boundaries"

          testCase "general Unicode ngrams do not compose kana marks"
          <| fun _ ->
              let collation = tryFind "utf8mb4_general_ci" |> Option.get
              for size in [ 3; 4 ] do
                  let rules = indexingRules (Ngrams size) StopwordPolicy.BuiltIn
                  let index =
                      buildIndexWithDocumentSettings rules collation
                          [ 1, rules, [ "ガラス" ]
                            2, rules, [ "カ\u3099ラス" ]
                            3, rules, [ "パピ" ]
                            4, rules, [ "ハ\u309aピ" ]
                            5, rules, [ "日本語日本語" ]
                            6, rules, [ "中文中文中文" ]
                            7, rules, [ "北海道東京" ]
                            8, rules, [ "韓国語学校" ] ]
                  let cases =
                      if size = 3 then
                          [ "ガラス", [ 1 ]; "カ\u3099ラス", [ 2 ]; "パピ", []; "ハ\u309aピ", [ 4 ] ]
                      else
                          [ "ガラス", []; "カ\u3099ラス", [ 2 ]; "パピ", []; "ハ\u309aピ", [] ]
                  for term, expected in cases do
                      for scores in [ naturalScores index term; booleanScores index term; expansionScores index term ] do
                          Expect.equal (scores |> Map.keys |> Seq.toList) expected (sprintf "MySQL size %d search for %A" size term)

          testCase "general Unicode radical postings do not inflate ideograph relevance"
          <| fun _ ->
              let rules = indexingRules (Ngrams 3) StopwordPolicy.BuiltIn
              let collation = tryFind "utf8mb4_general_ci" |> Option.get
              let index =
                  buildIndexWithDocumentSettings rules collation
                      [ 1, rules, [ "⽇本語"; "日本語" ]
                        2, rules, [ "日本語"; "東京" ]
                        3, rules, [ "中国語"; "学校" ]
                        4, rules, [ "韓国語"; "中文" ] ]
              for scores in [ naturalScores index "日本語"; booleanScores index "日本語" ] do
                  Expect.equal (scores |> Map.keys |> Seq.toList) [ 1; 2 ] "both documents have one ideograph posting"
                  for id in [ 1; 2 ] do
                      closeTo 0.0906190574169159 scores.[id] "MySQL does not count the radical as another ideograph"
              let radicalScores = naturalScores index "⽇本語"
              Expect.equal (radicalScores |> Map.keys |> Seq.toList) [ 1 ] "radical query has its own posting"
              closeTo 0.3624762296676636 radicalScores.[1] "radical relevance matches MySQL"

          testCase "general Unicode size-2 ngrams retain a decomposed accent"
          <| fun _ ->
              let rules = indexingRules (Ngrams 2) StopwordPolicy.BuiltIn
              let collation = tryFind "utf8mb4_general_ci" |> Option.get
              let index =
                  buildIndexWithDocumentSettings rules collation
                      [ 1, rules, [ "生e\u0301日"; "组合" ]
                        2, rules, [ "生日快乐"; "祝福" ]
                        3, rules, [ "日本語日本語"; "東京" ]
                        4, rules, [ "中文中文"; "学校" ] ]
              for query, expected in [ "日", []; "生 日", []; "\u0301日", [ 1 ]; "生e\u0301日", [ 1 ] ] do
                  for scores in [ naturalScores index query; booleanScores index query; expansionScores index query ] do
                      Expect.equal (scores |> Map.keys |> Seq.toList) expected (sprintf "MySQL search for %A" query)

          testCase "general Unicode size-2 ngrams keep kana forms separate"
          <| fun _ ->
              let rules = indexingRules (Ngrams 2) StopwordPolicy.BuiltIn
              let collation = tryFind "utf8mb4_general_ci" |> Option.get
              let index =
                  buildIndexWithDocumentSettings rules collation
                      [ 1, rules, [ "ゝゞヽヾ"; "かな" ]
                        2, rules, [ "ひらがなゟ"; "カタカナヿ" ]
                        3, rules, [ "日本語日本語"; "東京" ]
                        4, rules, [ "中文中文"; "学校" ] ]
              for query, expected in
                  [ "ひらがなゟ", [ 2 ]; "ゝゞ", [ 1 ]; "ヽヾ", [ 1 ]
                    "カタカナ", [ 2 ]; "カタカナヿ", [ 2 ] ] do
                  for scores in [ naturalScores index query; booleanScores index query; expansionScores index query ] do
                      Expect.equal (scores |> Map.keys |> Seq.toList) expected (sprintf "MySQL search for %A" query)

          testCase "boolean ngram terms retain Unicode number characters"
          <| fun _ ->
              let rules = indexingRules (Ngrams 1) StopwordPolicy.BuiltIn
              let index =
                  buildIndexWithDocumentSettings rules defaultCollation
                      [ 1, rules, [ "①一" ]; 2, rules, [ "Ⅳ四" ]; 3, rules, [ "zz" ] ]
              for query in [ "①"; "\"①\""; "+①"; "①*" ] do
                  Expect.equal (booleanScores index query |> Map.keys |> Seq.toList) [ 1 ] "circled digit remains a Boolean term"
              for query in [ "Ⅳ"; "\"Ⅳ\""; "+Ⅳ"; "Ⅳ*" ] do
                  Expect.equal (booleanScores index query |> Map.keys |> Seq.toList) [ 2 ] "Roman numeral remains a Boolean term"
              for query, id in [ "①一", 1; "\"①一\"", 1; "Ⅳ四", 2; "\"Ⅳ四\"", 2 ] do
                  Expect.equal (booleanScores index query |> Map.keys |> Seq.toList) [ id ] "adjacent ngrams retain both positions"

          testCase "word full-text indexes Unicode number tokens at the configured length"
          <| fun _ ->
              let index = buildIndexWith defaultCollation [ 1, "①①①"; 2, "ⅣⅣⅣ"; 3, "ordinary ordinary ordinary" ]
              Expect.equal (booleanScores index "①①①" |> Map.keys |> Seq.toList) [ 1 ] "circled digits form an indexed word"
              Expect.equal (booleanScores index "ⅣⅣⅣ" |> Map.keys |> Seq.toList) [ 2 ] "Roman numerals form an indexed word"
              Expect.isEmpty (booleanScores index "①") "the ordinary word-length minimum still applies"

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

          testCase "short word lookups reach indexed collation equivalents without changing phrase anchors"
          <| fun _ ->
              let index = buildIndexWith defaultCollation [ 1, "ffi"; 2, "orchard"; 3, "zzzz"; 4, "ﬃ" ]
              for score in [ naturalScores; booleanScores; expansionScores ] do
                  for query in [ "ﬃ"; "ﬃ ﬃ"; "+ﬃ" ] do
                      Expect.equal (score index query |> Map.keys |> Seq.toList) [ 1 ] "lookup does not apply the indexing minimum"
                  Expect.isEmpty (score index "\"ﬃ\"") "a short word alone cannot anchor a phrase"
                  Expect.equal (score index "\"ﬃ orchard\"" |> Map.keys |> Seq.toList) [ 2 ] "phrase anchors still skip short words"
              for scores in
                  [ tryNaturalSingleTermScoresDictionaryInView None (readView index) "ﬃ"
                    tryFlatBooleanScoresDictionaryInView None (readView index) "ﬃ" ] do
                  Expect.equal ((Option.get scores).Keys |> Seq.toList) [ 1 ] "optimized lookup uses the same bounds"

          testCase "repeated unweighted boolean words increase document frequency once per occurrence"
          <| fun _ ->
              let index = buildIndexWith defaultCollation [ 1, "ffi"; 2, "orchard"; 3, "zzzz"; 4, "ﬃ" ]
              for query, count in [ "ffi ffi", 2; "ﬃ ﬃ", 2; "ffi ﬃ", 2; "+ffi +ﬃ", 2; "ﬃ ﬃ ﬃ", 3 ] do
                  let expected = log10 (4.0 / float count) ** 2.0
                  closeTo expected (booleanScores index query).[1] "the posting contributes once using the accumulated document frequency"
                  let optimized = tryFlatBooleanScoresDictionaryInView None (readView index) query |> Option.get
                  closeTo expected optimized.[1] "optimized and general evaluation agree"

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
              let unfiltered = indexingRules defaultTokenizer StopwordPolicy.Disabled
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
              let filtered = indexingRules (Ngrams 2) StopwordPolicy.BuiltIn
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
              let filtered = indexingRules (Ngrams 2) StopwordPolicy.BuiltIn
              let unfiltered = { filtered with Stopwords = StopwordPolicy.Disabled }
              let baseline = buildIndexWithDocumentSettings filtered defaultCollation [ 1, filtered, [ "zz" ] ]
              let source = baseline |> withIndexingRules unfiltered |> addDocument 1 "ab"
              let target = baseline |> addDocument 2 "ab"
              let merged = mergeDocuments Map.empty baseline source target target
              Expect.equal (documentRules 1 merged) (Some unfiltered) "merged document keeps source indexing rules"
              Expect.equal (documentRules 2 merged) (Some { filtered with DocumentIndexing = DocumentIndexing.IncrementalNgram })
                  "concurrent document keeps the target's write-time stopword grouping"
              Expect.equal (activeRules merged) filtered "publication does not replace the target's active rules"
              Expect.equal (naturalScores merged "ab" |> Map.keys |> Seq.toList) [ 1 ] "only the unfiltered source document has the posting"

          testCase "stopword policy applies to word and ngram queries and later writes"
          <| fun _ ->
              for tokenizer, term in [ defaultTokenizer, "the"; Ngrams 2, "ab" ] do
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
              let index = buildIndexWithStopwords StopwordPolicy.Disabled defaultTokenizer defaultCollation [ 1, [ "a the orchard" ]; 2, [ "orchard" ] ]
              Expect.isEmpty (naturalScores index "a") "minimum token length still applies"
              Expect.equal (naturalScores index "the" |> Map.keys |> Seq.toList) [ 1 ] "the length-eligible stopword is searchable"

          testCase "document merging rebases inserted identities without replacing concurrent postings"
          <| fun _ ->
              let baseline = buildIndexWith defaultCollation [ 1, "orchard"; 2, "cobalt" ]
              let branch = baseline |> removeDocument 1 |> addDocument 3 "meadow"
              let concurrent = baseline |> addDocument 3 "forest"
              let merged = mergeDocuments (Map.ofList [ 3, 4 ]) baseline branch concurrent concurrent
              Expect.isEmpty (naturalScores merged "orchard") "the branch deletion is published"
              Expect.equal (naturalScores merged "meadow" |> Map.keys |> Seq.toList) [ 4 ] "the inserted document uses its rebased row ID"
              Expect.equal (naturalScores merged "forest" |> Map.keys |> Seq.toList) [ 3 ] "the concurrent insert retains its postings"
              Expect.isTrue (sameDocument 2 baseline merged) "unchanged document identity survives"
              Expect.isTrue (sameDocument 3 concurrent merged) "concurrent document identity survives"

          testCase "read visibility retains committed term frequencies for word and ngram scores"
          <| fun _ ->
              for tokenizer, term, other in [ defaultTokenizer, "orchard", "cobalt"; Ngrams 2, "生日", "中文" ] do
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

          testCase "ngram prefixes use per-word document frequency and the first matching word"
          <| fun _ ->
              let collation = tryFind "utf8mb4_bin" |> Option.get
              let build documents =
                  documents
                  |> List.map (fun (id, text) -> id, [ text ])
                  |> buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) collation

              let index = build [ 1, "東京 東北 東京"; 2, "東海"; 3, "大阪"; 4, "札幌" ]
              for query, first, second in
                  [ "東*", 0.031219376251101494, 0.015609688125550747
                    "+(東*)", 0.031219376251101494, 0.015609688125550747
                    "東* 東*", 0.062016263604164124, 0.031008131802082062
                    "+(東*) 東*", 0.062016263604164124, 0.031008131802082062
                    "(東* 東*)", 0.062016263604164124, 0.031008131802082062
                    "(東* 北*) 東*", 0.062016263604164124, 0.031008131802082062
                    "東* (-東*)", 0.031219376251101494, 0.015609688125550747
                    "東* (-東* 東海)", 0.062016263604164124, 0.031008131802082062
                    ">東* 東*", 1.062016248703, 1.031008124352
                    ">東* >東*", 1.062016248703, 1.031008124352
                    ">東* <東*", -0.937983751297, 0.031008131802
                    ">東* (<東*)", 1.062016248703, 1.031008124352
                    "東* ~東*", -0.937983751297, -0.968991875648
                    "~東* 東*", 0.062016263604164124, 0.031008131802082062 ] do
                  let scores = booleanScores index query
                  Expect.equal (scores |> Map.keys |> Seq.toList) [ 1; 2 ] query
                  closeTo first scores.[1] $"first matched document for {query}"
                  closeTo second scores.[2] $"second matched document for {query}"

              let bounded = tryFlatBooleanScoresDictionaryWithin (Some(set [ 2 ])) index "東*" |> Option.get
              Expect.equal (bounded.Keys |> Seq.toList) [ 2 ] "bounded prefix results"
              closeTo 0.015609688125550747 bounded.[2] "the bounded query keeps corpus-wide document frequency"

              let boundedGroup = booleanScoresWithin (set [ 1 ]) index "東* (-東* 東海)"
              closeTo 0.062016263604164124 boundedGroup.[1] "an excluded visit can depend on a row outside the candidate set"
              closeTo 0.090619057417 (booleanScores index "札幌 (-札幌)").[4] "an exact ngram visits its posting inside an empty exclusion group"
              Expect.isEmpty (booleanScores index "~東*") "a soft term cannot seed an ngram result"

              let frequent = build [ 1, "東京 東北 東京 東海 東西"; 2, "東南 東口"; 3, "大阪" ]
              let scores = booleanScores frequent "東*"
              closeTo 0.1812381148338318 scores.[1] "prefix document frequency may exceed the corpus size"
              closeTo 0.0906190574169159 scores.[2] "the second document uses the same prefix IDF"

              let replacement = index |> removeDocument 1 |> addDocument 2 "東海 東南" |> addDocument 5 "東京"
              let rebuilt = build [ 2, "東海 東南"; 3, "大阪"; 4, "札幌"; 5, "東京" ]
              Expect.equal (booleanScores replacement "東*") (booleanScores rebuilt "東*") "writes maintain the prefix posting counts"

          testCase "Boolean exact ngrams and prefixes share relevance under the same collation key"
          <| fun _ ->
              let documents =
                  [ 1, [ "生日\u034f快乐"; "生\u2060日" ]
                    2, [ "𠮷野\u0000家"; "生a日" ]
                    3, [ "①④ 中華"; "中国" ] ]
              let index =
                  buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) defaultCollation []
                  |> flushPostings
                  |> fun index -> List.fold (fun index (id, fields) -> addDocumentFields id fields index) index documents

              let queries =
                  [ "\"快\"", 0.22764469683170319
                    "快*", 0.031008131802082062
                    "\"快\" +快*", 1.885928302414186e-09
                    "快* \"快\"", 1.885928302414186e-09
                    "\"快\" 快*", 0.22764469683170319
                    "生日 \"快\" +快*", 0.22764469683170319 ]
              for query, expected in queries do
                  let scores = booleanScores index query
                  Expect.equal (scores |> Map.keys |> Seq.toList) [ 1 ] query
                  closeTo expected scores.[1] query

              let bounded = tryFlatBooleanScoresDictionaryWithin (Some(set [ 1 ])) index "\"快\" +快*" |> Option.get
              closeTo 1.885928302414186e-09 bounded.[1] "candidate bounds preserve shared frequency"

              let repeated =
                  buildIndexWithStopwords StopwordPolicy.Disabled (Ngrams 2) defaultCollation []
                  |> flushPostings
                  |> addDocument 1 "生日\u034f快乐 快乐 快乐"
                  |> addDocument 2 "\u034f快加 \u034f快加 \u034f快加"
                  |> addDocument 3 "大阪"
                  |> addDocument 4 "京都"
                  |> addDocument 5 "東京"
                  |> addDocument 6 "神戸"
                  |> addDocument 7 "松山"
              closeTo 0.004481872543692589 (booleanScores repeated "\"快\" +快*").[1] "the exact lookup visits the first row once"
              closeTo 0.013445617631077766 (booleanScores repeated "快* \"快\"").[1] "the prefix lookup visits the first row three times"

          testCase "Boolean short ngrams do not reach collapsed current-size postings"
          <| fun _ ->
              let collation = tryFind "utf8mb4_general_ci" |> Option.get
              let index = buildIndexWithTokenizer (Ngrams 2) collation [ 1, "中⵿文" ]
              let ids scores = scores |> Map.keys |> Seq.toList
              Expect.equal (booleanScores index "中ⴧ" |> ids) [] "MySQL rejects a truncated Boolean term"
              Expect.equal (booleanScores index "\"中ⴧ\"" |> ids) [] "quoted terms have the same boundary"
              let historical = buildIndexWithTokenizer (Ngrams 1) collation [ 2, "中文" ] |> withTokenizer (Ngrams 2)
              Expect.equal (booleanScores historical "中" |> ids) [ 2 ] "earlier one-character postings remain reachable"
          testCase "ngram exclusions without positive terms never score postings"
          <| fun _ ->
              let index =
                  buildIndexWithTokenizer (Ngrams 2) defaultCollation
                      [ 1, "生日快乐"; 2, "生日"; 3, "中文生日" ]

              for query in [ "-生日"; "-生日 -生日"; "-生日 -中文" ] do
                  Expect.isEmpty (booleanScores index query) (query + " has no positive match")
                  Expect.isEmpty
                      (tryFlatBooleanScoresDictionary index query |> Option.get)
                      (query + " has no flat-score candidate")

              Expect.equal
                  (booleanScores index "生日 -中文" |> Map.keys |> Seq.toList)
                  [ 1; 2 ]
                  "a mixed query still excludes matching ngram rows"
              Expect.isNone
                  (tryFlatBooleanScoresDictionary index "-\"生日快乐\"")
                  "a negative phrase still uses the general scorer"

          testCase "UTF-8 word searches classify documents before folding and queries after folding"
          <| fun _ ->
              let documents =
                  [ 1, "中華文㉎世界語"
                    2, "中華文𠀀東京駅"
                    3, "中華文Ɥ世界語"
                    4, "中華文ɥ世界語"
                    5, "中華文 世界語"
                    6, "cobalt'quartz"
                    7, "cobalt"
                    8, "quartz"
                    9, "cobalt quartz" ]

              let ids scores = scores |> Map.keys |> Seq.toList
              for collationName, foldedMatches in
                  [ "utf8mb4_bin", [ 1; 2; 3; 5 ]
                    "utf8mb4_0900_ai_ci", [ 4 ] ] do
                  let index = buildIndexWithTokenizer defaultTokenizer (tryFind collationName |> Option.get) documents
                  Expect.equal (ids (naturalScores index "中華文㉎世界語")) [ 1; 2; 3; 5 ] "newer BMP and supplementary characters separate words"
                  Expect.equal (ids (naturalScores index "中華文Ɥ世界語")) foldedMatches "natural query fold order"
                  Expect.equal (ids (booleanScores index "中華文Ɥ世界語")) foldedMatches "Boolean query fold order"
                  Expect.equal (ids (naturalScores index "\"cobalt'quartz\"")) [ 6; 9 ] "apostrophes separate words in natural phrases"
                  Expect.equal (ids (booleanScores index "+cobalt'quartz")) [ 6; 9 ] "the modifier applies to every word in the token group"
                  Expect.equal (ids (booleanScores index "\"cobalt'quartz\"")) [ 6; 9 ] "apostrophes separate words in Boolean phrases"

              let unicode9 = tryFind "utf8mb4_0900_ai_ci" |> Option.get
              let modern =
                  buildIndexWithTokenizer defaultTokenizer unicode9
                      [ 1, "中華文Ʂ世界語"; 2, "中華文ʂ世界語"; 3, "中華文 世界語" ]

              Expect.equal (ids (naturalScores modern "中華文Ʂ世界語")) [ 1; 3 ] "a post-Unicode-9 capital remains a separator"
              Expect.equal (ids (booleanScores modern "中華文Ʂ世界語")) [ 1; 3 ] "Unicode 9 also governs the Boolean boundary"

              let mb3General = tryFind "utf8mb3_general_ci" |> Option.get
              let accents =
                  buildIndexWithTokenizer defaultTokenizer mb3General
                      [ 1, "café"; 2, "cafe\u0301" ]
              Expect.equal (ids (naturalScores accents "cafe\u0301")) [ 2 ] "general_ci retains combining-mark distinctions"

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

          testCase "query expansion uses every first-pass document beyond twenty seeds"
          <| fun _ ->
              let documents =
                  [ for id in 1..25 do
                        let word = "uniqueword" + string (char (int 'a' + id - 1))
                        yield id, "orchard " + word
                        yield 100 + id, word
                    yield 999, "unrelated" ]
              let index = buildIndexWith defaultCollation documents
              let expected = Set.ofList ([ 1..25 ] @ [ 101..125 ])
              Expect.equal (expansionScores index "orchard" |> Map.keys |> Set.ofSeq) expected "InnoDB expands from all matching documents"
              Expect.equal (expansionScoresWithin (Set.singleton 125) index "orchard" |> Map.keys |> Set.ofSeq)
                  (Set.singleton 125) "predicate candidates do not truncate the seed pass"

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
                    "-database -security"
                    "missing -database"
                    "security -database"
                    "security -concurrency"
                    "data* concur*" ] do
                  let expected = booleanScores index query
                  let actual = tryFlatBooleanScoresDictionary index query |> Option.get |> asMap
                  Expect.equal actual expected (sprintf "flat scoring matches for %s" query)

              for query in [ "-database -security"; "database -security" ] do
                  Expect.isEmpty
                      (tryFlatBooleanScoresDictionaryWithin (Some Set.empty) index query |> Option.get)
                      (sprintf "empty candidate scope avoids matches for %s" query)

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
