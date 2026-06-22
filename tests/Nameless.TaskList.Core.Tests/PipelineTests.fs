module Nameless.TaskList.Core.Tests.PipelineTests

open System
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

// IMessageSource fake returning a single configured message.
type FakeMessages(msg: ChatMessage option) =
    interface IMessageSource with
        member _.GetMessage(_id, _jid) = msg
        member _.GetRecent(_jid, _before, _ex) = []

let sampleMessage () : ChatMessage =
    { Id = "M1"; ChatJid = "27800000000@s.whatsapp.net"; ChatName = "Wife"
      NormalizedChatName = "Wife"; IsGroup = false; SenderId = "27800000000"
      SenderName = "Wife"; SenderPushName = "Wife"; SenderSavedName = "Wife"
      SenderBusinessName = null; IsFromMe = false
      Content = "Can you call Acrobranch tomorrow about the 19th?"
      MediaType = null; FileName = null; AlbumId = null; AlbumIndex = None
      Timestamp = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc) }

let deps (messages: IMessageSource) (vault: FakeVault) (chat: IChatClient) : PipelineDeps =
    { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model" }

[<Fact>]
let ``returns NotFound when the message does not exist`` () =
    let d = deps (FakeMessages(None)) (FakeVault()) (FakeChatClient([]))
    Assert.Equal(NotFound, Pipeline.processMessage d "M1" "jid")

[<Fact>]
let ``noise message writes a minimal message file and creates no task`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"ack","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    let result = Pipeline.processMessage d "M1" "jid"
    Assert.Equal(ProcessedNoise, result)
    Assert.True(vault.Files.Keys |> Seq.exists (fun k -> k.StartsWith("messages/")))
    Assert.False(vault.Files.Keys |> Seq.exists (fun k -> k.StartsWith("tasks/")))

[<Fact>]
let ``signal message creates topic, task, and message referencing them`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"call the venue about the party date","action_required":true,"urgency":"high","people_mentioned":["wife"],"entities":{"tasks":["Call Acrobranch to confirm the 19th"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new subject","new_topic_title":"Ethan birthday party 2026"}"""
    let taskFile = Responses.final "---\ntype: Task\ntitle: Call Acrobranch\nstatus: pending\npriority: high\ncontext:\n  - family\n---\nCall the venue."
    let topicBody = Responses.final "## Current understanding\nParty being planned.\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; taskFile; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    let result = Pipeline.processMessage d "M1" "jid"
    match result with
    | Processed(topic, tasks) ->
        Assert.Equal("topics/active/ethan-birthday-party-2026.md", topic)
        Assert.Single(tasks) |> ignore
        Assert.True(vault.Files.ContainsKey(topic))
        Assert.True(vault.Files.ContainsKey(List.head tasks))
        Assert.True(vault.Files.Keys |> Seq.exists (fun k -> k.StartsWith("messages/")))
        // Assert the message file content references the topic and task paths.
        let msgKey = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("messages/"))
        let msgContent = vault.Files.[msgKey]
        Assert.Contains(topic, msgContent)
        Assert.Contains(List.head tasks, msgContent)
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``reprocessing an already-written message is a no-op`` () =
    let vault = FakeVault()
    // Pre-seed the message file at the path the pipeline will compute.
    vault.Seed("messages/wife/2026-06-15T14-17-45.md", "---\ntype: Message\n---\n")
    let chat = FakeChatClient([])  // must not be called
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Assert.Equal(Skipped, Pipeline.processMessage d "M1" "jid")
    Assert.Equal(0, chat.Calls)

[<Fact>]
let ``classification parse failure returns LlmError and writes nothing`` () =
    let vault = FakeVault()
    let chat = FakeChatClient([ Responses.final "I cannot help with that" ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | LlmError _ -> Assert.Empty(vault.Files.Keys)
    | other -> failwithf "expected LlmError, got %A" other

[<Fact>]
let ``signal message with an event writes a date-pathed event and links it`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["school"],"intent":"sports day","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":[],"events":["Ethan sports day on the 20th"],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new","new_topic_title":"Ethan sports day"}"""
    let eventFile = Responses.final "---\ntype: Event\ntitle: Ethan sports day\nwhen: 2026-06-20T09:00:00+02:00\nall_day: false\ncontext:\n  - school\n---\nAt the school field."
    let topicBody = Responses.final "## Current understanding\nSports day.\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; eventFile; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let eventPath = "events/2026/06/ethan-sports-day-2026-06-20.md"
        Assert.True(vault.Files.ContainsKey(eventPath))
        let msgKey = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("messages/"))
        Assert.Contains(eventPath, vault.Files.[msgKey])
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``event with no date falls back to the message date and is flagged`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"party","action_required":true,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":["A party sometime"],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Party"}"""
    let eventFile = Responses.final "---\ntype: Event\ntitle: A party\nall_day: true\ncontext:\n  - family\n---\nNo date given."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; eventFile; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    // sampleMessage timestamp is 2026-06-15
    let key = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("events/2026/06/a-party-2026-06-15"))
    Assert.Contains("inferred", vault.Files.[key])

[<Fact>]
let ``signal message with a commitment writes a commitment file`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["finance"],"intent":"fees due","action_required":true,"urgency":"high","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":["Q3 school fees due 1 July"],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new","new_topic_title":"School fees"}"""
    let commitmentFile = Responses.final "---\ntype: Commitment\ntitle: Q3 school fees\nstatus: unresolved\npriority: high\ndue: 2026-07-01\ncontext:\n  - finance\nescalate_after_days: 7\n---\nPay by EFT."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; commitmentFile; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    Assert.True(vault.Files.ContainsKey("commitments/q3-school-fees.md"))
    Assert.Contains("unresolved", vault.Files.["commitments/q3-school-fees.md"])

[<Fact>]
let ``signal message with a note writes a note file and links it`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["medical"],"intent":"allergy fact","action_required":false,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":["Ethan is allergic to penicillin"]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new","new_topic_title":"Ethan health"}"""
    let noteFile = Responses.final "---\ntype: Note\ntitle: Ethan penicillin allergy\ncontext:\n  - medical\ntags:\n  - allergy\n---\nConfirmed 2023."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; noteFile; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    let notePath = "notes/ethan-penicillin-allergy.md"
    Assert.True(vault.Files.ContainsKey(notePath))
    let msgKey = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("messages/"))
    Assert.Contains(notePath, vault.Files.[msgKey])
