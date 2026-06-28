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

let private topicFile (title: string) =
    sprintf "---\ntype: Topic\ntitle: %s\nstatus: active\n---\n## Current understanding\nThe estate gate is faulty.\n" title

[<Fact>]
let ``Steps.matchTopic matches an existing topic via shortlist + confirm`` () =
    let vault = FakeVault()
    vault.Seed("topics/active/13th-street-gate-fault.md", topicFile "13th Street gate fault")
    // Constant embedding => cosine = 1.0, clears the floor.
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |])
    let chat = FakeChatClient([ Responses.final """{"match":true,"topic_slug":"13th-street-gate-fault","confidence":0.9,"match_reason":"same gate","new_topic_title":null}""" ])
    match Steps.matchTopic (chat :> IChatClient) (embedder :> IEmbedder) (vault :> IVault) 5 0.5 "the gate motor is slow again" with
    | Ok (Steps.MatchExisting slug) -> Assert.Equal("13th-street-gate-fault", slug)
    | other -> failwithf "expected MatchExisting, got %A" other

[<Fact>]
let ``Steps.matchTopic creates a new topic when no topic files exist`` () =
    let vault = FakeVault()   // no topics/active at all -> tool-enabled fallback
    let embedder = FakeEmbedder(fun _ -> [| 1.0 |])
    let chat = FakeChatClient([ Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"School fees 2026"}""" ])
    match Steps.matchTopic (chat :> IChatClient) (embedder :> IEmbedder) (vault :> IVault) 5 0.5 "school fees are due" with
    | Ok (Steps.CreateTopic title) -> Assert.Equal("School fees 2026", title)
    | other -> failwithf "expected CreateTopic, got %A" other
