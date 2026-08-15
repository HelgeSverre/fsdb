module Fsdb.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    Tests.runTestsWithCLIArgs
        []
        argv
        (testList
            "fsdb"
            [ PacketTests.tests
              ProtocolTests.tests
              QueryHandlerTests.tests
              PreparedStatementTests.tests
              TransactionTests.tests
              ServerTests.tests
              ValueTests.tests
              ParserTests.tests
              StorageTests.tests
              PersistenceTests.tests
              ExecutorTests.tests
              InformationSchemaTests.tests
              IntegrationTests.tests ])
