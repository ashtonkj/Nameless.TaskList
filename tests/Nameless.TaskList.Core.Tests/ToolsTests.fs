module Nameless.TaskList.Core.Tests.ToolsTests

open System.Collections.Generic
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

[<Fact>]
let ``getContexts returns content of files under contexts`` () =
    let vault = FakeVault()
    vault.Seed("contexts/family.md", "---\ntype: Context\ntitle: Family\n---\nFamily stuff")
    let tool = Tools.getContexts (vault :> IVault)
    let result = tool.Handler (Dictionary<string, obj>())
    Assert.Contains("Family", result)

[<Fact>]
let ``getTopic returns the requested topic body`` () =
    let vault = FakeVault()
    vault.Seed("topics/active/birthday.md", "---\ntype: Topic\n---\nCake not ordered")
    let tool = Tools.getTopic (vault :> IVault)
    let args = Dictionary<string, obj>()
    args.["slug"] <- box "birthday"
    let result = tool.Handler args
    Assert.Contains("Cake not ordered", result)

[<Fact>]
let ``getContexts exposes a named tool definition`` () =
    let vault = FakeVault()
    let tool = Tools.getContexts (vault :> IVault)
    Assert.Equal("get_contexts", tool.Definition.Function.Name)
