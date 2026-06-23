module Nameless.TaskList.Core.Tests.PipelineTests

open System
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

// IMessageSource fake returning a single configured message.
type FakeMessages(msg: ChatMessage option, ?media: byte array) =
    interface IMessageSource with
        member _.GetMessage(_id, _jid) = msg
        member _.GetRecent(_jid, _before, _ex) = []
        member _.GetMessagesSince(_chatJid, _since) = []
        member _.GetMediaBytes(_id, _jid) = media

let sampleMessage () : ChatMessage =
    { Id = "M1"; ChatJid = "27800000000@s.whatsapp.net"; ChatName = "Wife"
      NormalizedChatName = "Wife"; IsGroup = false; SenderId = "27800000000"
      SenderName = "Wife"; SenderPushName = "Wife"; SenderSavedName = "Wife"
      SenderBusinessName = null; IsFromMe = false
      Content = "Can you call Acrobranch tomorrow about the 19th?"
      MediaType = null; FileName = null; AlbumId = null; AlbumIndex = None
      Timestamp = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc) }

// Default embedder throws — existing tests seed no active topics, so the topic-match
// step never calls it (and if it ever did, the pipeline falls back gracefully).
let deps (messages: IMessageSource) (vault: FakeVault) (chat: IChatClient) : PipelineDeps =
    { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
      Embedder = FakeEmbedder(fun _ -> failwith "no embedder configured") :> IEmbedder
      TopK = 5; SimilarityFloor = 0.5
      Vision = FakeVision(fun _ -> failwith "no vision configured") :> IVision }

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

[<Fact>]
let ``mentioned unknown person gets a stub`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["medical"],"intent":"see doctor","action_required":true,"urgency":"medium","people_mentioned":["Dr Naidoo"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new","new_topic_title":"Doctor visit"}"""
    let personStub = Responses.final "---\ntype: Person\ntitle: Dr Naidoo\nrole: Paediatrician\ncontext:\n  - medical\n---\nEthan's paediatrician. ⚠ Stub — details to be completed."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; personStub; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    Assert.True(vault.Files.ContainsKey("people/medical/dr-naidoo.md"))

[<Fact>]
let ``existing person is not overwritten and no stub LLM call is made`` () =
    let vault = FakeVault()
    vault.Seed("people/medical/dr-naidoo.md", "---\ntype: Person\ntitle: Dr Naidoo\n---\nExisting.")
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["medical"],"intent":"see doctor","action_required":true,"urgency":"medium","people_mentioned":["Dr Naidoo"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new","new_topic_title":"Doctor visit"}"""
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    // No personStub response scripted: if the pipeline tried to create one, the queue would underflow.
    let chat = FakeChatClient([ classify; topicMatch; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    Assert.Equal("---\ntype: Person\ntitle: Dr Naidoo\n---\nExisting.", vault.Files.["people/medical/dr-naidoo.md"])
    Assert.Equal(3, chat.Calls)

[<Fact>]
let ``person stub with out-of-list context is clamped to family`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"mentioned coach","action_required":false,"urgency":"low","people_mentioned":["Coach Smith"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new","new_topic_title":"Coaching"}"""
    let personStub = Responses.final "---\ntype: Person\ntitle: Coach Smith\nrole: Sports coach\ncontext:\n  - personal-kb\n---\nCoach Smith. ⚠ Stub — details to be completed."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; personStub; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    // The stub context "personal-kb" is not in knownContexts, so it should be clamped to "family".
    Assert.True(vault.Files.ContainsKey("people/family/coach-smith.md"))
    Assert.False(vault.Files.ContainsKey("people/personal-kb/coach-smith.md"))

// ── Channel update tests ────────────────────────────────────────────────────

let private emptySignalClassify =
    Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"hi","action_required":false,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""

let private newTopicMatch (title: string) =
    Responses.final (sprintf """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"%s"}""" title)

[<Fact>]
let ``signal message creates a channel with direct platform and records the topic`` () =
    let vault = FakeVault()
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ emptySignalClassify; newTopicMatch "Family chat"; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    let ch = vault.Files.["channels/whatsapp/wife.md"]   // sampleMessage NormalizedChatName = "Wife"
    Assert.Contains("platform: whatsapp-direct", ch)
    Assert.Contains("message_count: 1", ch)
    Assert.Contains("last_processed:", ch)                 // activity timestamp recorded
    Assert.Contains("topics/active/family-chat.md", ch)   // active_topics holds the topic

[<Fact>]
let ``noise message creates a channel and counts it without a topic`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"ack","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    let ch = vault.Files.["channels/whatsapp/wife.md"]
    Assert.Contains("platform: whatsapp-direct", ch)
    Assert.Contains("message_count: 1", ch)
    Assert.DoesNotContain("topics/active", ch)

[<Fact>]
let ``existing channel increments count, dedupes topic, and preserves body`` () =
    let vault = FakeVault()
    // Pre-seed a channel that already counted 5 messages and already lists the topic.
    vault.Seed("channels/whatsapp/wife.md",
        "---\ntype: Channel\ntitle: Wife\nplatform: whatsapp-direct\ncontext: ''\npeople: []\nsignal_weight: high\nmessage_count: 5\nlast_processed: 2026-06-10T00:00:00\nactive_topics:\n  - topics/active/family-chat.md\n---\nExisting channel notes.")
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ emptySignalClassify; newTopicMatch "Family chat"; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    let ch = vault.Files.["channels/whatsapp/wife.md"]
    Assert.Contains("message_count: 6", ch)
    Assert.Contains("Existing channel notes.", ch)
    // topic appears exactly once (deduped)
    let occurrences = (ch.Split("topics/active/family-chat.md").Length - 1)
    Assert.Equal(1, occurrences)

// A caption-less image message (empty content, media_type=image).
let private imageMessage () : ChatMessage =
    { sampleMessage () with Content = ""; MediaType = "image" }

[<Fact>]
let ``image-only message is described by vision and processed as that text`` () =
    let vault = FakeVault()
    // classify the vision text as a signal task, new topic, one task, topic update
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"birthday party invite","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":["RSVP to the party"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Birthday party"}"""
    let taskFile = Responses.final "---\ntype: Task\ntitle: RSVP to the party\nstatus: pending\npriority: medium\ncontext:\n  - family\n---\nrsvp"
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; taskFile; topicBody ])
    let messages = FakeMessages(Some(imageMessage ()), [| 1uy; 2uy; 3uy |]) :> IMessageSource
    let vision = FakeVision(fun _ -> "INVITE: Ethan's party Saturday 2pm, please RSVP") :> IVision
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5
              Vision = vision }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let msgKey = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("messages/"))
        Assert.Contains("vision-extracted", vault.Files.[msgKey])           // body header
        Assert.Contains("INVITE: Ethan's party", vault.Files.[msgKey])      // the description is the content
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``image-only message falls back to noise when vision fails`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"empty","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    let messages = FakeMessages(Some(imageMessage ()), [| 1uy; 2uy |]) :> IMessageSource
    let vision = FakeVision(fun _ -> failwith "vision down") :> IVision
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5
              Vision = vision }
    // vision throws -> content stays empty -> classify (scripted noise) -> ProcessedNoise, no crash
    Assert.Equal(ProcessedNoise, Pipeline.processMessage d "M1" "jid")

// ── Hybrid embedding topic-match tests ─────────────────────────────────────

let private depsE (vault: FakeVault) (chat: IChatClient) (embedder: IEmbedder) (topK: int) (floor: float) : PipelineDeps =
    { Messages = FakeMessages(Some(sampleMessage ())) :> IMessageSource
      Vault = vault :> IVault; Chat = chat; Model = "test-model"
      Embedder = embedder; TopK = topK; SimilarityFloor = floor
      Vision = FakeVision(fun _ -> failwith "no vision configured") :> IVision }

// Seed one active topic with a known slug + understanding.
let private seedTopic (v: FakeVault) (slug: string) (title: string) (understanding: string) =
    v.Seed(sprintf "topics/active/%s.md" slug,
           sprintf "---\ntype: Topic\ntitle: %s\nstatus: active\ncontext:\n  - family\n---\n## Current understanding\n%s\n\n## Open questions\n\n## Resolved\n" title understanding)

let private signalClassify =
    Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"the birthday party plan","action_required":true,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""

let private topicBody = Responses.final "## Current understanding\nx\n\n## Open questions\n\n## Resolved\n"

[<Fact>]
let ``embedding shortlists a similar topic and the LLM confirms the match`` () =
    let vault = FakeVault()
    seedTopic vault "birthday-party" "Birthday party" "planning the party"
    // intent vector close to the birthday topic's vector
    let embedder = FakeEmbedder(fun t -> if t.Contains("birthday") || t.Contains("Birthday") || t.Contains("party") then [| 1.0; 0.0 |] else [| 0.0; 1.0 |]) :> IEmbedder
    // classify, then the topic-match LLM confirm (matches the candidate), then topic update
    let confirm = Responses.final """{"match":true,"topic_slug":"birthday-party","confidence":0.9,"match_reason":"same","new_topic_title":null}"""
    let chat = FakeChatClient([ signalClassify; confirm; topicBody ])
    let d = depsE vault chat embedder 5 0.5
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, _) -> Assert.Equal("topics/active/birthday-party.md", topic)
    | other -> failwithf "expected Processed match, got %A" other

[<Fact>]
let ``no topic above the floor creates a new topic without an LLM topic-match call`` () =
    let vault = FakeVault()
    seedTopic vault "unrelated" "Unrelated" "something else entirely"
    // every embedding orthogonal -> cosine 0 < floor -> empty shortlist -> fast path
    let embedder = FakeEmbedder(fun t -> if t.Contains("birthday") then [| 1.0; 0.0 |] else [| 0.0; 1.0 |]) :> IEmbedder
    // Only classify + topic update are scripted; a topic-match LLM call would underflow the queue.
    let chat = FakeChatClient([ signalClassify; topicBody ])
    let d = depsE vault chat embedder 5 0.5
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, _) ->
        Assert.StartsWith("topics/active/", topic)
        Assert.DoesNotContain("unrelated", topic)        // a NEW topic, not the seeded one
    | other -> failwithf "expected Processed new, got %A" other

[<Fact>]
let ``LLM returning a slug outside the shortlist creates a new topic`` () =
    let vault = FakeVault()
    seedTopic vault "birthday-party" "Birthday party" "planning the party"
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder   // everything similar
    let halluc = Responses.final """{"match":true,"topic_slug":"not-a-candidate","confidence":0.9,"match_reason":"x","new_topic_title":"Fresh topic"}"""
    let chat = FakeChatClient([ signalClassify; halluc; topicBody ])
    let d = depsE vault chat embedder 5 0.5
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, _) -> Assert.Equal("topics/active/fresh-topic.md", topic)   // from NewTopicTitle, not the hallucinated slug
    | other -> failwithf "expected Processed new, got %A" other

[<Fact>]
let ``embedder failure falls back to the tool-enabled topic match`` () =
    let vault = FakeVault()
    seedTopic vault "birthday-party" "Birthday party" "planning the party"
    let embedder = FakeEmbedder(fun _ -> failwith "embed down") :> IEmbedder
    // fallback path runs the tool-enabled LLM topic match (1 call), here returning a match
    let fallbackMatch = Responses.final """{"match":true,"topic_slug":"birthday-party","confidence":0.9,"match_reason":"same","new_topic_title":null}"""
    let chat = FakeChatClient([ signalClassify; fallbackMatch; topicBody ])
    let d = depsE vault chat embedder 5 0.5
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, _) -> Assert.Equal("topics/active/birthday-party.md", topic)
    | other -> failwithf "expected Processed match via fallback, got %A" other

[<Fact>]
let ``LLM confirming with wrong casing still matches the real candidate slug`` () =
    let vault = FakeVault()
    seedTopic vault "birthday-party" "Birthday party" "planning the party"
    // intent and topic map to the same vector (cosine 1.0) — well above the floor
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder
    // LLM echoes the slug with mixed casing — the guard must normalise before comparing
    let confirm = Responses.final """{"match":true,"topic_slug":"Birthday-Party","confidence":0.9,"match_reason":"same","new_topic_title":null}"""
    let chat = FakeChatClient([ signalClassify; confirm; topicBody ])
    let d = depsE vault chat embedder 5 0.5
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, _) -> Assert.Equal("topics/active/birthday-party.md", topic)   // real slug, not a new duplicate
    | other -> failwithf "expected Processed match, got %A" other

[<Fact>]
let ``accepted entity reply backfills type and pipeline-owned linkage`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"do the thing","action_required":true,"urgency":"high","people_mentioned":[],"entities":{"tasks":["Do the thing"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"My topic"}"""
    // Model reply OMITS type, topic, source_message — as gemma did on the live run.
    let taskFile = Responses.final "---\ntitle: Do the thing\nstatus: pending\npriority: high\ncontext:\n  - family\n---\nbody"
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; taskFile; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, tasks) ->
        let content = vault.Files.[List.head tasks]
        Assert.Contains("type: Task", content)        // pipeline owns the type tag
        Assert.Contains(topic, content)               // topic path backfilled
        Assert.Contains("messages/", content)         // source_message backfilled
    | other -> failwithf "expected Processed, got %A" other
