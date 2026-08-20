module Fsdb.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    Fsdb.Log.silence ()

    Tests.runTestsWithCLIArgs
        []
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
              VectorTests.tests ])
