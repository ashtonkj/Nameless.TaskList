module Nameless.TaskList.Core.Tests.StepsTests

open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

let private classifyJson =
    """{"noise":false,"contexts":["family"],"intent":"Wife asks to call the school",
        "action_required":true,"urgency":"medium",
        "people_mentioned":["Nancy","pookie"],
        "entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""

[<Fact>]
let ``Steps.classify parses and strips endearments`` () =
    let chat = FakeChatClient([ Responses.final classifyJson ])
    let vault = FakeVault()
    match Steps.classify (chat :> IChatClient) (vault :> IVault) "" "Can you call the school?" with
    | Ok c ->
        Assert.False(c.Noise)
        Assert.Equal<string array>([| "Nancy" |], c.PeopleMentioned)   // "pookie" dropped
    | Error e -> failwithf "expected Ok, got Error %s" e.Message

[<Fact>]
let ``Steps.classify returns Error with raw on unparseable reply`` () =
    let chat = FakeChatClient([ Responses.final "I cannot help with that." ])
    let vault = FakeVault()
    match Steps.classify (chat :> IChatClient) (vault :> IVault) "" "hello" with
    | Ok _ -> failwith "expected Error"
    | Error e -> Assert.Contains("cannot help", e.Raw)
