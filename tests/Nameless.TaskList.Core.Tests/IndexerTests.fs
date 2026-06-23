module Nameless.TaskList.Core.Tests.IndexerTests

open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

let private seedCore () =
    let v = FakeVault()
    v.Seed("tasks/pending/high.md", "---\ntype: Task\ntitle: Urgent\nstatus: pending\npriority: high\ndue: 2026-07-01\ncontext:\n  - medical\n---\nbody")
    v.Seed("tasks/pending/low.md", "---\ntype: Task\ntitle: Whenever\nstatus: pending\npriority: low\ndue: ''\ncontext:\n  - family\n---\nbody")
    v.Seed("topics/active/t1.md", "---\ntype: Topic\ntitle: Topic One\nstatus: active\nlast_updated: 2026-06-15\n---\nbody")
    v.Seed("channels/whatsapp/wife.md", "---\ntype: Channel\ntitle: Wife\nplatform: whatsapp-direct\nsignal_weight: high\nmessage_count: 3\nlast_processed: 2026-06-15T00:00:00\nactive_topics:\n  - topics/active/t1.md\n---\nbody")
    v.Seed("tasks/pending/bad.md", "no frontmatter here")   // malformed → skipped
    v

[<Fact>]
let ``regenerate writes a tasks index ordering high priority before low`` () =
    let v = seedCore ()
    Indexer.regenerate (v :> IVault) |> ignore
    let idx = v.Files.["tasks/index.md"]
    // Frontmatter must serialize (regression: a private IndexMeta produced an empty `{}`).
    Assert.Contains("type: Index", idx)
    Assert.Contains("title: Task Index", idx)
    Assert.Contains("[[tasks/pending/high]]", idx)
    Assert.Contains("[[tasks/pending/low]]", idx)
    Assert.True(idx.IndexOf("tasks/pending/high") < idx.IndexOf("tasks/pending/low"))

[<Fact>]
let ``regenerate writes topic and channel indexes`` () =
    let v = seedCore ()
    Indexer.regenerate (v :> IVault) |> ignore
    Assert.Contains("[[topics/active/t1]]", v.Files.["topics/index.md"])
    Assert.Contains("[[channels/whatsapp/wife]]", v.Files.["channels/index.md"])

[<Fact>]
let ``regenerate counts items and skips malformed files`` () =
    let v = seedCore ()
    let s = Indexer.regenerate (v :> IVault)
    Assert.Equal(2, s.Tasks)        // high + low; bad.md skipped
    Assert.Equal(1, s.Topics)
    Assert.Equal(1, s.Channels)
    Assert.Equal(1, s.Skipped)

let private seedRest () =
    let v = FakeVault()
    v.Seed("events/2026/06/early.md", "---\ntype: Event\ntitle: Early\nwhen: 2026-06-01T09:00:00+02:00\ncontext:\n  - family\n---\nb")
    v.Seed("events/2026/07/late.md", "---\ntype: Event\ntitle: Late\nwhen: 2026-07-01T09:00:00+02:00\ncontext:\n  - family\n---\nb")
    v.Seed("commitments/fees.md", "---\ntype: Commitment\ntitle: Fees\nstatus: unresolved\npriority: high\ndue: 2026-07-01\ntask_assigned: ''\n---\nb")
    v.Seed("notes/allergy.md", "---\ntype: Note\ntitle: Allergy\ncontext:\n  - medical\ntags:\n  - allergy\n---\nb")
    v.Seed("people/medical/dr-naidoo.md", "---\ntype: Person\ntitle: Dr Naidoo\nrole: Paediatrician\ncontext:\n  - medical\n---\nb")
    v

[<Fact>]
let ``regenerate writes events index in chronological order`` () =
    let v = seedRest ()
    Indexer.regenerate (v :> IVault) |> ignore
    let idx = v.Files.["events/index.md"]
    Assert.True(idx.IndexOf("events/2026/06/early") < idx.IndexOf("events/2026/07/late"))

[<Fact>]
let ``regenerate puts undated events last in events index`` () =
    let v = FakeVault()
    v.Seed("events/2026/06/dated.md", "---\ntype: Event\ntitle: Dated\nwhen: 2026-06-01T09:00:00+02:00\ncontext:\n  - family\n---\nb")
    v.Seed("events/2026/06/undated.md", "---\ntype: Event\ntitle: Undated\nwhen: ''\ncontext:\n  - family\n---\nb")
    Indexer.regenerate (v :> IVault) |> ignore
    let idx = v.Files.["events/index.md"]
    Assert.True(idx.IndexOf("events/2026/06/dated") < idx.IndexOf("events/2026/06/undated"))

[<Fact>]
let ``regenerate flags commitments with no assigned task`` () =
    let v = seedRest ()
    Indexer.regenerate (v :> IVault) |> ignore
    Assert.Contains("[[commitments/fees]]", v.Files.["commitments/index.md"])
    Assert.Contains("⚑", v.Files.["commitments/index.md"])

[<Fact>]
let ``regenerate writes notes and people indexes and full counts`` () =
    let v = seedRest ()
    let s = Indexer.regenerate (v :> IVault)
    Assert.Contains("[[notes/allergy]]", v.Files.["notes/index.md"])
    Assert.Contains("[[people/medical/dr-naidoo]]", v.Files.["people/index.md"])
    Assert.Equal(2, s.Events)
    Assert.Equal(1, s.Commitments)
    Assert.Equal(1, s.Notes)
    Assert.Equal(1, s.People)
