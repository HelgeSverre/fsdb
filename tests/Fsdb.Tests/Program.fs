module Fsdb.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    Fsdb.Log.silence ()
    TestSupport.configureForArguments argv

    try
        Tests.runTestsWithCLIArgs
            [ Tests.CLIArguments.Fail_On_Focused_Tests ]
            argv
            (testList
                "fsdb"
                [ LimitsTests.tests
                  PacketTests.tests
                  ProtocolTests.tests
                  QueryHandlerTests.tests
                  PreparedStatementTests.tests
                  TransactionTests.tests
                  ServerTests.tests
                  ValueTests.tests
                  ParserTests.tests
                  ParserCommentFuzzTests.tests
                  StorageTests.tests
                  AuthTests.tests
                  FullTextTests.tests
                  FullTextExecutorTests.tests
                  PersistenceTests.tests
                  ExecutorTests.tests
                  TriggerTests.tests
                  ViewTests.tests
                  CheckConstraintTests.tests
                  InformationSchemaTests.tests
                  TemporalPrecisionTests.tests
                  IntegrationTests.tests
                  EmbeddingIntegrationTests.tests
                  VectorTests.tests ])
    finally
        TestSupport.cleanup ()
