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
