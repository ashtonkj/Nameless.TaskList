module Nameless.TaskList.IntegrationTests.AdapterIntegrationTests

open System
open Xunit
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.IntegrationTests.Support

// No live service needed — proves the harness executes under -p:Integration=true.
[<Fact>]
let ``FileSystemVault round-trips write, read, exists and list`` () =
    Helpers.withTempVault (fun root ->
        let vault = FileSystemVault(root) :> IVault
        vault.Write("topics/active/sample.md", "hello body")
        Assert.True(vault.Exists "topics/active/sample.md")
        Assert.Equal("hello body", vault.Read "topics/active/sample.md")
        Assert.Contains("topics/active/sample.md", vault.ListFilesRecursive "topics"))
