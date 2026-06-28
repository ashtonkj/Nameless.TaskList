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

let private genInput intent : Steps.GenInput =
    { Intent = intent; Raw = intent; ReferenceDate = "2026-06-24T12:00:00+02:00"
      Contexts = [| "medical" |]; Urgency = "medium"
      TopicPath = "topics/active/t.md"; MessagePath = "messages/m.md"
      PeopleSlugs = [||]; TaskPaths = [] }

[<Fact>]
let ``Steps.createTask parses a model task and stamps linkage`` () =
    let md = "---\ntype: Task\ntitle: Book flu vaccine\nstatus: pending\npriority: medium\ndue: 2026-07-03\ncontext: [medical]\npeople: []\n---\nBook the jab.\n"
    let chat = FakeChatClient([ Responses.final md ])
    let o = Steps.createTask (chat :> IChatClient) (genInput "Book flu vaccine")
    Assert.Equal("Book flu vaccine", o.Record.Title)
    Assert.Equal("pending", o.Record.Status)
    Assert.Equal("2026-07-03", o.Record.Due)
    Assert.Equal("topics/active/t.md", o.Record.Topic)        // linkage stamped from input
    Assert.Equal("messages/m.md", o.Record.SourceMessage)

[<Fact>]
let ``Steps.createTask falls back on unparseable reply`` () =
    let chat = FakeChatClient([ Responses.final "sorry, I cannot do that" ])
    let o = Steps.createTask (chat :> IChatClient) (genInput "Pay the fees")
    Assert.Equal("Pay the fees", o.Record.Title)              // fallback = intent
    Assert.Equal("pending", o.Record.Status)
    Assert.Equal("medium", o.Record.Priority)

[<Fact>]
let ``Steps.createEvent infers date and flags body when when is missing`` () =
    let md = "---\ntype: Event\ntitle: School picnic\nall_day: true\ncontext: [school]\n---\nBring a teddy.\n"
    let chat = FakeChatClient([ Responses.final md ])
    let o = Steps.createEvent (chat :> IChatClient) (genInput "School picnic")
    Assert.Equal("2026-06-24T12:00:00+02:00", o.Record.When)  // fell back to reference date
    Assert.Contains("Date inferred", o.Body)

[<Fact>]
let ``Steps.createCommitment parses status unresolved`` () =
    let md = "---\ntype: Commitment\ntitle: Return the form\nstatus: unresolved\npriority: medium\ndue: 2026-07-01\ncontext: [school]\n---\nOwe the school a signed form.\n"
    let chat = FakeChatClient([ Responses.final md ])
    let o = Steps.createCommitment (chat :> IChatClient) (genInput "Return the form")
    Assert.Equal("unresolved", o.Record.Status)
    Assert.Equal("2026-07-01", o.Record.Due)

[<Fact>]
let ``Steps.createNote parses a note and stamps source`` () =
    let md = "---\ntype: Note\ntitle: Medical aid details\ncontext: [medical]\ntags: [insurance]\n---\n## Medical aid\nPolicy 12345.\n"
    let chat = FakeChatClient([ Responses.final md ])
    let o = Steps.createNote (chat :> IChatClient) (genInput "Medical aid number is 12345")
    Assert.Equal("Medical aid details", o.Record.Title)
    Assert.Equal<string array>([| "medical" |], o.Record.Context)
    Assert.Equal("messages/m.md", o.Record.Source)            // linkage from input
    Assert.Contains("Policy 12345", o.Body)

[<Fact>]
let ``Steps.matchTask confirms a shortlisted candidate`` () =
    let vault = FakeVault()
    vault.Seed("tasks/pending/sign-up-ethan-for-swimming.md",
               "---\ntype: Task\ntitle: Sign up Ethan for swimming\nstatus: pending\n---\nRegister Ethan.\n")
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |])   // cosine 1.0 clears the floor
    let chat = FakeChatClient([ Responses.final """{"match":true,"topic_slug":"sign-up-ethan-for-swimming","confidence":0.9,"match_reason":"same","new_topic_title":null}""" ])
    match Steps.matchTask (chat :> IChatClient) (embedder :> IEmbedder) (vault :> IVault) "Register Ethan for swimming" 0.5 5 with
    | Some slug -> Assert.Equal("sign-up-ethan-for-swimming", slug)
    | None -> failwith "expected a match"

[<Fact>]
let ``Steps.matchNote returns None when the model declines`` () =
    let vault = FakeVault()
    vault.Seed("notes/medical-aid-details.md", "---\ntype: Note\ntitle: Medical aid details\n---\nPolicy.\n")
    let embedder = FakeEmbedder(fun _ -> [| 1.0 |])
    let chat = FakeChatClient([ Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"different","new_topic_title":null}""" ])
    Assert.Equal(None, Steps.matchNote (chat :> IChatClient) (embedder :> IEmbedder) (vault :> IVault) "Buy a birthday gift" 0.5 5)

[<Fact>]
let ``Steps.matchTask returns None with no candidates`` () =
    let vault = FakeVault()
    let embedder = FakeEmbedder(fun _ -> [| 1.0 |])
    let chat = FakeChatClient([])
    Assert.Equal(None, Steps.matchTask (chat :> IChatClient) (embedder :> IEmbedder) (vault :> IVault) "anything" 0.5 5)

[<Fact>]
let ``Steps.updateTask parses the merged task`` () =
    let md = "---\ntype: Task\ntitle: Pay school fees\nstatus: pending\npriority: high\ndue: 2026-07-01\ncontext: [finance]\npeople: []\n---\nPay the term 3 fees.\n"
    let chat = FakeChatClient([ Responses.final md ])
    match Steps.updateTask (chat :> IChatClient) "existing file" "fees due 1 July" "raw" with
    | Ok o -> Assert.Equal("high", o.Record.Priority); Assert.Equal("2026-07-01", o.Record.Due)
    | Error e -> failwithf "expected Ok, got Error %s" e

[<Fact>]
let ``Steps.updateTask returns Error with raw on unparseable reply`` () =
    let chat = FakeChatClient([ Responses.final "no frontmatter here" ])
    match Steps.updateTask (chat :> IChatClient) "existing" "i" "r" with
    | Ok _ -> failwith "expected Error"
    | Error raw -> Assert.Contains("no frontmatter here", raw)

[<Fact>]
let ``Steps.updateNote returns the stripped body`` () =
    let chat = FakeChatClient([ Responses.final "```\n## Medical aid\nPolicy 12345, expires 2027.\n```" ])
    let body = Steps.updateNote (chat :> IChatClient) "old body" "expiry 2027" "raw"
    Assert.Contains("expires 2027", body)
    Assert.DoesNotContain("```", body)

let private personInput name (contexts: string array) : Steps.GenInput =
    { Intent = name; Raw = ""; ReferenceDate = ""; Contexts = contexts; Urgency = ""
      TopicPath = ""; MessagePath = "messages/m.md"; PeopleSlugs = [||]; TaskPaths = [] }

[<Fact>]
let ``Steps.createPersonStub parses a model person`` () =
    let md = "---\ntype: Person\ntitle: Dr Brown\nrole: dentist\ncontext: [medical]\naliases: [Brown]\n---\nEthan's dentist. ⚠ Stub — details to be completed.\n"
    let chat = FakeChatClient([ Responses.final md ])
    let o = Steps.createPersonStub (chat :> IChatClient) (personInput "Dr Brown" [| "medical" |])
    Assert.Equal("Dr Brown", o.Record.Title)
    Assert.Equal("dentist", o.Record.Role)

[<Fact>]
let ``Steps.createPersonStub falls back to a single role-derived context`` () =
    let chat = FakeChatClient([ Responses.final "unparseable" ])
    let o = Steps.createPersonStub (chat :> IChatClient) (personInput "Coach Brian" [| "school"; "family" |])
    Assert.Equal("Coach Brian", o.Record.Title)
    Assert.Equal<string array>([| "school" |], o.Record.Context)   // first known context
    Assert.Contains("Stub", o.Body)
