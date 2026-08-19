module Fsdb.Tests.FullTextTests

open Expecto
open Fsdb.FullText

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
        [ testCase "tokenizer: word chars, in-word apostrophes, case folding, punctuation"
          <| fun _ ->
              Expect.equal (tokenize "Never run mysqld as root!") [| "never"; "run"; "mysqld"; "as"; "root" |] "plain words"
              Expect.equal (tokenize "O'Brien's DB_2, 'quoted'") [| "o'brien's"; "db_2"; "quoted" |] "apostrophes and underscore"
              Expect.equal (tokenize "") [||] "empty"

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

          testCase "boolean: + requires and - excludes"
          <| fun _ ->
              let s = booleanScoresOf corpus "+MySQL -YourSQL"
              let matched = s |> Array.mapi (fun i v -> i + 1, v > 0.0) |> Array.filter snd |> Array.map fst
              Expect.equal matched [| 1; 2; 3; 4; 6 |] "YourSQL row excluded"

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

              let near = booleanScoresOf corpus "\"stands database\" @5"
              Expect.equal (near |> Array.mapi (fun i v -> i + 1, v > 0.0) |> Array.filter snd |> Array.map fst) [| 1 |] "proximity window"

              let far = booleanScoresOf corpus "\"stands database\" @1"
              Expect.equal (far |> Array.mapi (fun i v -> i + 1, v > 0.0) |> Array.filter snd |> Array.map fst) [||] "window too narrow"

          testCase "boolean: ~ keeps the row but zeroes the term's contribution"
          <| fun _ ->
              let s = booleanScoresOf corpus "~security MySQL"
              // Doc 6 contains security, but its score is just mysql's 2ε.
              closeTo (s.[0] * 2.0) s.[5] "soft negation contributes nothing"
              Expect.all (List.ofArray s |> List.map (fun v -> v > 0.0)) id "no row excluded"

          testCase "query expansion ranks the seed docs first and pulls in term-sharing docs"
          <| fun _ ->
              // Oracle: AGAINST ('database' WITH QUERY EXPANSION) returns all
              // six rows ordered 1, 5, 3, 6, 2, 4.
              let s = expansionScoresOf corpus "database"
              Expect.all (List.ofArray s |> List.map (fun v -> v > 0.0)) id "expansion reaches every doc"

              let order =
                  s |> Array.mapi (fun i v -> i + 1, v) |> Array.sortByDescending snd |> Array.map fst

              Expect.equal order.[0] 1 "the direct match ranks first"
              Expect.equal order.[1] 5 "the other direct match second" ]
