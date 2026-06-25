module Nameless.TaskList.Core.Tests.PipelineTests

open System
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline
open Nameless.TaskList.Core.Conversation
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

// IMessageSource fake returning a single configured message, optional media bytes,
// and an optional recent-history list (newest-first, as the real GetRecent returns).
type FakeMessages(msg: ChatMessage option, ?media: byte array, ?recent: ChatMessage list) =
    let recentList = defaultArg recent []
    interface IMessageSource with
        member _.GetMessage(_id, _jid) = msg
        member _.GetRecent(_jid, _before, _ex) = recentList
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
      TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5; TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
      Vision = FakeVision(fun _ -> failwith "no vision configured") :> IVision
      Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber configured") :> ITranscriber }

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
let ``two identical task intents in one message produce exactly one task file`` () =
    // Regression: writeEntities used freePath, so a re-extracted identical task created a
    // tasks/pending/<slug>-2.md duplicate instead of overwriting the same-titled file.
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"sign up for the club","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":["Sign up for Holiday Club","Sign up for Holiday Club"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Holiday club"}"""
    let taskFile = Responses.final "---\ntype: Task\ntitle: Sign up for Holiday Club\nstatus: pending\npriority: medium\ncontext:\n  - family\n---\nSign up for the club."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    // two task intents -> two taskFile generations, then the topic-update body
    let chat = FakeChatClient([ classify; topicMatch; taskFile; taskFile; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let taskFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("tasks/")) |> List.ofSeq
        Assert.Equal(1, taskFiles.Length)   // exactly one task file — no -2.md duplicate
        Assert.True(vault.Files.ContainsKey("tasks/pending/sign-up-for-holiday-club.md"))
        Assert.False(vault.Files.ContainsKey("tasks/pending/sign-up-for-holiday-club-2.md"))
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``empty-content message (caption-less video) is noise with no LLM call`` () =
    // Video/document with no caption yields no vision/transcription text; classifying empty
    // input just makes the model chat back. Short-circuit to noise before any LLM call.
    let vault = FakeVault()
    let msg = { sampleMessage () with Content = ""; MediaType = "video" }
    let chat = FakeChatClient([])   // empty queue: any classify call would throw
    let d = deps (FakeMessages(Some msg)) vault chat
    let result = Pipeline.processMessage d "M1" "jid"
    Assert.Equal(ProcessedNoise, result)
    Assert.Equal(0, chat.Calls)
    Assert.True(vault.Files.Keys |> Seq.exists (fun k -> k.StartsWith("messages/")))

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
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5; TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
              Vision = vision
              Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber configured") :> ITranscriber }
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
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5; TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
              Vision = vision
              Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber configured") :> ITranscriber }
    // vision throws -> content stays empty -> classify (scripted noise) -> ProcessedNoise, no crash
    Assert.Equal(ProcessedNoise, Pipeline.processMessage d "M1" "jid")

[<Fact>]
let ``vision-derived noise preserves the text`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"ack","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    let messages = FakeMessages(Some(imageMessage ()), [| 1uy; 2uy; 3uy |]) :> IMessageSource
    let vision = FakeVision(fun _ -> "SCREENSHOT: random meme text") :> IVision
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5; TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
              Vision = vision
              Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber configured") :> ITranscriber }
    match Pipeline.processMessage d "M1" "jid" with
    | ProcessedNoise ->
        let msgKey = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("messages/"))
        let msgContent = vault.Files.[msgKey]
        Assert.Contains("vision-extracted", msgContent)           // body header preserved
        Assert.Contains("SCREENSHOT: random meme text", msgContent)  // the description is preserved
    | other -> failwithf "expected ProcessedNoise, got %A" other

[<Fact>]
let ``GetMediaBytes=None falls back to noise`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"empty","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    // FakeMessages with an image message but NO media bytes (None)
    let messages = FakeMessages(Some(imageMessage ())) :> IMessageSource
    let vision = FakeVision(fun _ -> failwith "should not be called") :> IVision
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5; TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
              Vision = vision
              Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber configured") :> ITranscriber }
    // GetMediaBytes returns None -> vision is not called -> content stays empty -> classify (scripted noise) -> ProcessedNoise
    Assert.Equal(ProcessedNoise, Pipeline.processMessage d "M1" "jid")

let private audioMessage () : ChatMessage =
    { sampleMessage () with Content = ""; MediaType = "audio" }

[<Fact>]
let ``voice-note is transcribed and processed as that text`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"party rsvp request","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":["RSVP to the party"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Birthday party"}"""
    let taskFile = Responses.final "---\ntype: Task\ntitle: RSVP to the party\nstatus: pending\npriority: medium\ncontext:\n  - family\n---\nrsvp"
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; taskFile; topicBody ])
    let messages = FakeMessages(Some(audioMessage ()), [| 9uy; 8uy; 7uy |]) :> IMessageSource
    let transcriber = FakeTranscriber(fun _ -> "Please RSVP to Ethan's party on Saturday at 2pm") :> ITranscriber
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5; TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
              Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
              Transcriber = transcriber }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let msgKey = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("messages/"))
        Assert.Contains("Voice note (transcribed)", vault.Files.[msgKey])     // body header
        Assert.Contains("Please RSVP to Ethan's party", vault.Files.[msgKey]) // transcript is the content
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``voice-note falls back to noise when transcription fails`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"empty","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    let messages = FakeMessages(Some(audioMessage ()), [| 1uy; 2uy |]) :> IMessageSource
    let transcriber = FakeTranscriber(fun _ -> failwith "whisper down") :> ITranscriber
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5; TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
              Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
              Transcriber = transcriber }
    // transcription throws -> content stays empty -> classify (scripted noise) -> ProcessedNoise, no crash
    Assert.Equal(ProcessedNoise, Pipeline.processMessage d "M1" "jid")

[<Fact>]
let ``transcribed noise preserves the text`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"chitchat","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    let messages = FakeMessages(Some(audioMessage ()), [| 3uy; 2uy; 1uy |]) :> IMessageSource
    let transcriber = FakeTranscriber(fun _ -> "just saying hi, talk later") :> ITranscriber
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5; TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
              Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
              Transcriber = transcriber }
    match Pipeline.processMessage d "M1" "jid" with
    | ProcessedNoise ->
        let msgKey = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("messages/"))
        let msgContent = vault.Files.[msgKey]
        Assert.Contains("Voice note (transcribed)", msgContent)  // header preserved
        Assert.Contains("just saying hi, talk later", msgContent) // transcript preserved
    | other -> failwithf "expected ProcessedNoise, got %A" other

[<Fact>]
let ``audio GetMediaBytes=None falls back to noise`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"empty","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    // audio message but NO stored media bytes (None) -> transcriber must not be called
    let messages = FakeMessages(Some(audioMessage ())) :> IMessageSource
    let transcriber = FakeTranscriber(fun _ -> failwith "should not be called") :> ITranscriber
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5; TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
              Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
              Transcriber = transcriber }
    Assert.Equal(ProcessedNoise, Pipeline.processMessage d "M1" "jid")

// ── Hybrid embedding topic-match tests ─────────────────────────────────────

let private depsE (vault: FakeVault) (chat: IChatClient) (embedder: IEmbedder) (topK: int) (floor: float) : PipelineDeps =
    { Messages = FakeMessages(Some(sampleMessage ())) :> IMessageSource
      Vault = vault :> IVault; Chat = chat; Model = "test-model"
      Embedder = embedder; TopK = topK; SimilarityFloor = floor; NoteTopK = 5; NoteSimilarityFloor = 0.5; TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
      Vision = FakeVision(fun _ -> failwith "no vision configured") :> IVision
      Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber configured") :> ITranscriber }

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

[<Fact>]
let ``classify call includes recent conversation history`` () =
    let vault = FakeVault()
    // A distinctive prior message the classifier should receive as context.
    let prior =
        { sampleMessage () with
            Id = "M0"; SenderName = "Wife"
            Content = "Are you free for Ethan's party on the 19th?" }
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"confirm party date","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Ethan party"}"""
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; topicBody ])
    let messages = FakeMessages(Some(sampleMessage ()), recent = [ prior ])
    let d = deps messages vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    // First Chat call is classify; index 0 is the system message, index 1 the user message.
    let classifyUserMsg = (chat.Received.[0].[1] :?> UserMessage).Content
    Assert.Contains("Are you free for Ethan's party on the 19th?", classifyUserMsg)
    Assert.Contains("Message to classify:", classifyUserMsg)

[<Fact>]
let ``topic-update call also receives recent conversation history`` () =
    let vault = FakeVault()
    let prior =
        { sampleMessage () with
            Id = "M0"; SenderName = "Wife"
            Content = "Are you free for Ethan's party on the 19th?" }
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"confirm party date","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Ethan party"}"""
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; topicBody ])
    let messages = FakeMessages(Some(sampleMessage ()), recent = [ prior ])
    let d = deps messages vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    // Third Chat call (index 2) is the topic-update; index 1 is its user message.
    let topicUpdateUserMsg = (chat.Received.[2].[1] :?> UserMessage).Content
    Assert.Contains("Are you free for Ethan's party on the 19th?", topicUpdateUserMsg)
    Assert.Contains("Recent conversation", topicUpdateUserMsg)

// A message whose sender mentions one person, with configurable people_mentioned.
let private personMessage () : ChatMessage =
    { sampleMessage () with Content = "Took Ethan to see the doctor today" }

let private personDeps (messages: IMessageSource) (vault: FakeVault) (chat: IChatClient) : PipelineDeps =
    { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
      Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5; TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
      Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
      Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }

let private seedPerson (vault: FakeVault) (path: string) (title: string) (context: string) (aliases: string list) =
    let aliasYaml = if List.isEmpty aliases then "[]" else "[" + (aliases |> List.map (fun a -> sprintf "\"%s\"" a) |> String.concat ", ") + "]"
    vault.Seed(path, sprintf "---\ntype: Person\ntitle: %s\nrole: spouse\ncontext: [%s]\nchannel: \"\"\nphone: \"\"\nemail: \"\"\ntags: []\naliases: %s\n---\nstub\n" title context aliasYaml)

[<Fact>]
let ``person mention matching a recorded alias does not create a duplicate`` () =
    let vault = FakeVault()
    seedPerson vault "people/family/sarah-smith.md" "Sarah Smith" "family" ["Mom"]
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"note from mom","action_required":false,"urgency":"low","people_mentioned":["Mom"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Note from mom"}"""
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    // No person-stub response scripted: the mention must resolve to the existing alias and skip the LLM.
    let chat = FakeChatClient([ classify; topicMatch; topicBody ])
    let d = personDeps (FakeMessages(Some(personMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let peopleFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("people/")) |> List.ofSeq
        Assert.Equal<string list>([ "people/family/sarah-smith.md" ], peopleFiles)
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``new surface form whose canonical title matches an existing person appends an alias`` () =
    let vault = FakeVault()
    seedPerson vault "people/family/sarah-smith.md" "Sarah Smith" "family" []
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"sarah update","action_required":false,"urgency":"low","people_mentioned":["Sarah"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Sarah update"}"""
    let personStub = Responses.final "---\ntype: Person\ntitle: Sarah Smith\nrole: spouse\ncontext: [family]\naliases: []\n---\nSarah is the owner's wife. ⚠ Stub — details to be completed."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; personStub; topicBody ])
    let d = personDeps (FakeMessages(Some(personMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let peopleFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("people/")) |> List.ofSeq
        Assert.Equal<string list>([ "people/family/sarah-smith.md" ], peopleFiles)   // no new file
        Assert.Contains("Sarah", vault.Files.["people/family/sarah-smith.md"])        // alias recorded
        Assert.Contains("aliases", vault.Files.["people/family/sarah-smith.md"])
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``a doctor is filed under the medical context, not family`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"saw the paediatrician","action_required":false,"urgency":"low","people_mentioned":["Dr Naidoo"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Paediatrician visit"}"""
    let personStub = Responses.final "---\ntype: Person\ntitle: Dr Naidoo\nrole: paediatrician\ncontext: [medical]\naliases: []\n---\nEthan's paediatrician. ⚠ Stub — details to be completed."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; personStub; topicBody ])
    let d = personDeps (FakeMessages(Some(personMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        Assert.True(vault.Files.ContainsKey("people/medical/dr-naidoo.md"))
        Assert.False(vault.Files.ContainsKey("people/family/dr-naidoo.md"))
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``two mentions resolving to the same canonical person produce one file with both aliases`` () =
    // No people seeded — both "Mom" and "Sarah" are unknown.
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"message about sarah","action_required":false,"urgency":"low","people_mentioned":["Mom","Sarah"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Sarah update"}"""
    // First personStub: surface "Mom" -> canonical title "Sarah Smith"
    let personStubMom = Responses.final "---\ntype: Person\ntitle: Sarah Smith\nrole: spouse\ncontext:\n  - family\naliases: []\n---\nSarah Smith. ⚠ Stub — details to be completed."
    // Second personStub: surface "Sarah" -> canonical title "Sarah Smith" (same canonical)
    let personStubSarah = Responses.final "---\ntype: Person\ntitle: Sarah Smith\nrole: spouse\ncontext:\n  - family\naliases: []\n---\nSarah Smith. ⚠ Stub — details to be completed."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; personStubMom; personStubSarah; topicBody ])
    let d = personDeps (FakeMessages(Some(personMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        // Exactly one people file must exist
        let peopleFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("people/")) |> List.ofSeq
        Assert.Equal<string list>([ "people/family/sarah-smith.md" ], peopleFiles)
        // Both surface forms must appear in the file (as aliases)
        let content = vault.Files.["people/family/sarah-smith.md"]
        Assert.Contains("Mom", content)
        Assert.Contains("Sarah", content)
    | other -> failwithf "expected Processed, got %A" other

// ── Note match-and-merge tests ──────────────────────────────────────────────

let private noteDeps (messages: IMessageSource) (vault: FakeVault) (chat: IChatClient) : PipelineDeps =
    { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
      Embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder   // constant vector => cosine 1.0, always shortlisted
      TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.35
      TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
      Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
      Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }

[<Fact>]
let ``a note matching an existing note merges into it instead of creating a new file`` () =
    let vault = FakeVault()
    vault.Seed("notes/medical-aid-details.md", "---\ntype: Note\ntitle: Medical aid details\ncontext: [medical]\npeople_linked: []\ntags: []\nsource: \"\"\nlast_verified: \"\"\n---\n## Membership\nDiscovery Health, plan Classic.\n")
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["medical"],"intent":"medical aid membership number is 12345","action_required":false,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":["Discovery Health membership number 12345"]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Medical aid number"}"""
    let noteMatch = Responses.final """{"match":true,"topic_slug":"medical-aid-details","confidence":0.9,"match_reason":"same note","new_topic_title":null}"""
    let noteMerged = Responses.final "## Membership\nDiscovery Health, plan Classic. Membership number 12345."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; noteMatch; noteMerged; topicBody ])
    let d = noteDeps (FakeMessages(Some(sampleMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let noteFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("notes/")) |> List.ofSeq
        Assert.Equal<string list>([ "notes/medical-aid-details.md" ], noteFiles)   // no new note file
        Assert.Contains("12345", vault.Files.["notes/medical-aid-details.md"])     // merged content
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``merged note body has fences stripped before being written to the file`` () =
    let vault = FakeVault()
    vault.Seed("notes/medical-aid-details.md", "---\ntype: Note\ntitle: Medical aid details\ncontext: [medical]\npeople_linked: []\ntags: []\nsource: \"\"\nlast_verified: \"\"\n---\n## Membership\nDiscovery Health, plan Classic.\n")
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["medical"],"intent":"medical aid membership number is 12345","action_required":false,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":["Discovery Health membership number 12345"]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Medical aid number"}"""
    let noteMatch = Responses.final """{"match":true,"topic_slug":"medical-aid-details","confidence":0.9,"match_reason":"same note","new_topic_title":null}"""
    // Model wraps the merged body in markdown fences — the pipeline must strip them.
    let noteMerged = Responses.final "```markdown\n## Membership\nDiscovery Health, plan Classic. Membership number 12345.\n```"
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; noteMatch; noteMerged; topicBody ])
    let d = noteDeps (FakeMessages(Some(sampleMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let content = vault.Files.["notes/medical-aid-details.md"]
        Assert.DoesNotContain("```", content)        // fences must not leak into the stored file
        Assert.Contains("12345", content)            // merged fact must be present
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``a note with no existing notes creates a new note file`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["medical"],"intent":"record allergy","action_required":false,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":["Ethan is allergic to penicillin"]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Allergy"}"""
    let noteFile = Responses.final "---\ntype: Note\ntitle: Ethan penicillin allergy\ncontext: [medical]\ntags: [allergy]\n---\nEthan is allergic to penicillin."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    // Empty notes dir => no shortlist => no noteMatch call; just noteCreate.
    let chat = FakeChatClient([ classify; topicMatch; noteFile; topicBody ])
    let d = noteDeps (FakeMessages(Some(sampleMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        Assert.True(vault.Files.ContainsKey("notes/ethan-penicillin-allergy.md"))
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``two note intents in the same message about the same subject produce exactly one note file`` () =
    // Regression: the stale existingNotes listing (computed once before the loop) means the second intent
    // does not see the note created by the first and creates notes/medical-aid-details-2.md instead of
    // merging into notes/medical-aid-details.md.
    let vault = FakeVault()
    // No notes seeded — the vault is empty at the start.
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["medical"],"intent":"Discovery Health medical aid info","action_required":false,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":["Discovery Health membership number 12345","Discovery Health plan is Classic"]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new","new_topic_title":"Medical aid details"}"""
    // Note intent 1: vault is empty -> no shortlist -> noteCreate path.
    let noteCreate1 = Responses.final "---\ntype: Note\ntitle: Medical aid details\ncontext:\n  - medical\ntags: []\n---\nDiscovery Health, membership number 12345."
    // Note intent 2: after the fix the listing is re-scanned, finding the note just created above,
    // embedding shortlists it (FakeEmbedder always returns [|1.0; 0.0|]), and noteMatch fires.
    let noteMatch2 = Responses.final """{"match":true,"topic_slug":"medical-aid-details","confidence":0.9,"match_reason":"same","new_topic_title":null}"""
    // noteUpdate merges the second fact into the existing note body.
    let noteUpdate2 = Responses.final "## Membership\nDiscovery Health, membership number 12345. Plan is Classic."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    // Fixed call sequence: classify, topicMatch, noteCreate1, noteMatch2, noteUpdate2, topicBody.
    let chat = FakeChatClient([ classify; topicMatch; noteCreate1; noteMatch2; noteUpdate2; topicBody ])
    let d = noteDeps (FakeMessages(Some(sampleMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let noteFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("notes/")) |> List.ofSeq
        Assert.Equal(1, noteFiles.Length)   // exactly one note file — no -2.md duplicate
        Assert.True(vault.Files.ContainsKey("notes/medical-aid-details.md"))
        Assert.False(vault.Files.ContainsKey("notes/medical-aid-details-2.md"))
        Assert.Contains("Classic", vault.Files.["notes/medical-aid-details.md"])   // merged content
    | other -> failwithf "expected Processed, got %A" other

// ── Relationship extraction tests ───────────────────────────────────────────

[<Fact>]
let ``processMessage writes a relationship edge when two known people are co-mentioned`` () =
    let vault = FakeVault()
    // Seed two existing people so both mentions resolve; person-stub step skips them (no LLM call).
    vault.Seed("people/family/ethan.md", "---\ntype: Person\ntitle: Ethan\n---\nbody")
    vault.Seed("people/medical/dr-naidoo.md", "---\ntype: Person\ntitle: Dr Naidoo\n---\nbody")
    // Two co-mentioned people with no entity intents — call order: classify, topicMatch, relationshipExtract, topicBody.
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["medical"],"intent":"doctor visit","action_required":false,"urgency":"low","people_mentioned":["Ethan","Dr Naidoo"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Doctor visit"}"""
    let relationshipExtract = Responses.final """{"relationships":[{"from":"ethan","to":"dr-naidoo","relation":"patient-doctor","descriptor":"paeds","confidence":"high"}]}"""
    let topicBody = Responses.final "## Current understanding\nEthan saw Dr Naidoo.\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; relationshipExtract; topicBody ])
    let d = personDeps (FakeMessages(Some(personMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        // Canonical alphabetical path: dr-naidoo before ethan
        Assert.True(vault.Files.ContainsKey("relationships/dr-naidoo-ethan.md"))
        Assert.Contains("relation: patient-doctor", vault.Files.["relationships/dr-naidoo-ethan.md"])
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``processMessage writes no relationship edge when only one person resolves`` () =
    let vault = FakeVault()
    // Seed only ONE existing person file so only one mention resolves.
    vault.Seed("people/family/ethan.md", "---\ntype: Person\ntitle: Ethan\n---\nbody")
    // One person mentioned with no entity intents — call order: classify, topicMatch, topicBody (no relationship step).
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"about ethan","action_required":false,"urgency":"low","people_mentioned":["Ethan"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Ethan note"}"""
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    // Only 3 responses scripted; no relationshipExtract response because the step must not fire.
    let chat = FakeChatClient([ classify; topicMatch; topicBody ])
    let d = personDeps (FakeMessages(Some(personMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        // Assert no relationship file was written
        Assert.False(vault.Files.Keys |> Seq.exists (fun k -> k.StartsWith("relationships/")))
    | other -> failwithf "expected Processed, got %A" other

// ── Task match-and-merge tests ───────────────────────────────────────────────

[<Fact>]
let ``two matching task intents in one message produce one updated task file`` () =
    let vault = FakeVault()
    // Constant embedder => the first-written task is always shortlisted for the second intent.
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder
    let d =
        { Messages = FakeMessages(Some(sampleMessage ())); Vault = vault :> IVault
          Chat = Unchecked.defaultof<IChatClient>; Model = "test-model"
          Embedder = embedder; TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5
          TaskTopK = 5; TaskSimilarityFloor = 0.35; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
          Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
          Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"sign up","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":["Sign up for the club","Register for the club"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Club"}"""
    let taskCreate1 = Responses.final "---\ntype: Task\ntitle: Sign up for the club\nstatus: pending\npriority: medium\ndue: ''\ncontext:\n  - family\n---\nSign up for the club."
    // Second intent: vault now has the first task -> shortlist -> taskMatch=true (same slug) -> taskUpdate.
    let taskMatch2 = Responses.final """{"match":true,"topic_slug":"sign-up-for-the-club","confidence":0.9,"match_reason":"same action","new_topic_title":null}"""
    let taskUpdate2 = Responses.final "---\ntype: Task\ntitle: Sign up for the club\nstatus: pending\npriority: high\ndue: 2026-07-01\ncontext:\n  - family\n---\nSign up / register for the club before the deadline."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; taskCreate1; taskMatch2; taskUpdate2; topicBody ])
    let d = { d with Chat = chat }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let taskFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("tasks/")) |> List.ofSeq
        Assert.Equal(1, taskFiles.Length)
        Assert.True(vault.Files.ContainsKey("tasks/pending/sign-up-for-the-club.md"))
        Assert.False(vault.Files.ContainsKey("tasks/pending/register-for-the-club.md"))
        Assert.Contains("register", vault.Files.["tasks/pending/sign-up-for-the-club.md"])
        Assert.Contains("priority: high", vault.Files.["tasks/pending/sign-up-for-the-club.md"])   // raised, not downgraded
        Assert.Contains("2026-07-01", vault.Files.["tasks/pending/sign-up-for-the-club.md"])        // due filled from new mention
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``two distinct task intents both create files (no false match)`` () =
    let vault = FakeVault()
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"mattress","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":["Buy a mattress","Research mattresses"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Mattress"}"""
    let taskCreate1 = Responses.final "---\ntype: Task\ntitle: Buy a mattress\nstatus: pending\npriority: medium\ndue: ''\ncontext:\n  - family\n---\nBuy a mattress."
    let taskMatch2 = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"different action","new_topic_title":"Research mattresses"}"""
    let taskCreate2 = Responses.final "---\ntype: Task\ntitle: Research mattresses\nstatus: pending\npriority: medium\ndue: ''\ncontext:\n  - family\n---\nResearch mattresses."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; taskCreate1; taskMatch2; taskCreate2; topicBody ])
    let d =
        { Messages = FakeMessages(Some(sampleMessage ())); Vault = vault :> IVault
          Chat = chat; Model = "test-model"; Embedder = embedder
          TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5
          TaskTopK = 5; TaskSimilarityFloor = 0.35; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
          Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
          Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let taskFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("tasks/")) |> List.ofSeq
        Assert.Equal(2, taskFiles.Length)
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``first task in an empty vault skips the embedder and matcher`` () =
    let vault = FakeVault()
    // Embedder throws if called; an empty tasks/pending must skip the shortlist entirely.
    let embedder = FakeEmbedder(fun _ -> failwith "embedder must not be called") :> IEmbedder
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"call","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":["Call the school"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"School"}"""
    let taskCreate = Responses.final "---\ntype: Task\ntitle: Call the school\nstatus: pending\npriority: medium\ndue: ''\ncontext:\n  - family\n---\nCall the school."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; taskCreate; topicBody ])
    let d =
        { Messages = FakeMessages(Some(sampleMessage ())); Vault = vault :> IVault
          Chat = chat; Model = "test-model"; Embedder = embedder
          TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5
          TaskTopK = 5; TaskSimilarityFloor = 0.35; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
          Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
          Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) -> Assert.True(vault.Files.ContainsKey("tasks/pending/call-the-school.md"))
    | other -> failwithf "expected Processed, got %A" other

// ── People fuzzy match-and-merge tests ─────────────────────────────────────

[<Fact>]
let ``a fuzzy-matched person mention adds an alias instead of a duplicate stub`` () =
    let vault = FakeVault()
    // Seed an existing person "Nancy".
    vault.Seed("people/school/nancy.md", "---\ntype: Person\ntitle: Nancy\nrole: Teacher\naliases: []\n---\nEthan's teacher.")
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder   // constant => Nancy is shortlisted
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["school"],"intent":"note from teacher","action_required":false,"urgency":"low","people_mentioned":["Teacher Nancy"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"School note"}"""
    // people step: "teacher-nancy" does not exact-resolve -> fuzzy shortlist -> personMatch=true (nancy).
    let personMatch = Responses.final """{"match":true,"topic_slug":"nancy","confidence":0.9,"match_reason":"same person","new_topic_title":null}"""
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; personMatch; topicBody ])
    let d =
        { Messages = FakeMessages(Some(sampleMessage ())); Vault = vault :> IVault
          Chat = chat; Model = "test-model"; Embedder = embedder
          TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5
          TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.35
          Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
          Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let peopleFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("people/")) |> List.ofSeq
        Assert.Equal(1, peopleFiles.Length)                                  // no new stub
        Assert.Contains("Teacher Nancy", vault.Files.["people/school/nancy.md"])  // alias added
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``a different person still creates a separate stub`` () =
    let vault = FakeVault()
    vault.Seed("people/school/nancy.md", "---\ntype: Person\ntitle: Nancy\nrole: Teacher\naliases: []\n---\nTeacher.")
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"call dad","action_required":false,"urgency":"low","people_mentioned":["Trevor"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Call"}"""
    let personMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"different person","new_topic_title":null}"""
    let personStub = Responses.final "---\ntype: Person\ntitle: Trevor\nrole: ''\ncontext:\n  - family\naliases: []\n---\nTrevor."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; personMatch; personStub; topicBody ])
    let d =
        { Messages = FakeMessages(Some(sampleMessage ())); Vault = vault :> IVault
          Chat = chat; Model = "test-model"; Embedder = embedder
          TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5
          TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.35
          Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
          Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        Assert.True(vault.Files.Keys |> Seq.exists (fun k -> k.StartsWith("people/") && k.Contains("trevor")))
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``a mention that exactly resolves skips the fuzzy matcher`` () =
    let vault = FakeVault()
    vault.Seed("people/school/nancy.md", "---\ntype: Person\ntitle: Nancy\nrole: Teacher\naliases: []\n---\nTeacher.")
    // Embedder throws if the fuzzy path runs; an exact resolve must short-circuit before it.
    let embedder = FakeEmbedder(fun _ -> failwith "embedder must not be called") :> IEmbedder
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["school"],"intent":"note","action_required":false,"urgency":"low","people_mentioned":["Nancy"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Note"}"""
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; topicBody ])
    let d =
        { Messages = FakeMessages(Some(sampleMessage ())); Vault = vault :> IVault
          Chat = chat; Model = "test-model"; Embedder = embedder
          TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5
          TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.35
          Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
          Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) -> Assert.Equal(1, vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("people/")) |> Seq.length)
    | other -> failwithf "expected Processed, got %A" other
