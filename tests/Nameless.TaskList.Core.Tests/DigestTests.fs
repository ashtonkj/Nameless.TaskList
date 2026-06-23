module Nameless.TaskList.Core.Tests.DigestTests

open System
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Digest
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

let private today = DateTime(2026, 6, 23)

let private seed () =
    let v = FakeVault()
    // Two pending tasks; medical+soon should outrank family+far.
    v.Seed("tasks/pending/urgent.md", "---\ntype: Task\ntitle: Urgent\nstatus: pending\npriority: high\ndue: 2026-06-24\ncontext:\n  - medical\n---\nb")
    v.Seed("tasks/pending/later.md", "---\ntype: Task\ntitle: Later\nstatus: pending\npriority: low\ndue: ''\ncontext:\n  - family\n---\nb")
    v.Seed("tasks/pending/bad.md", "no frontmatter")                 // skipped
    // Events: one inside the 7-day window, one outside.
    v.Seed("events/2026/06/soon.md", "---\ntype: Event\ntitle: Soon\nwhen: 2026-06-25T09:00:00+02:00\ncontext:\n  - family\n---\nb")
    v.Seed("events/2026/07/far.md", "---\ntype: Event\ntitle: Far\nwhen: 2026-07-20T09:00:00+02:00\ncontext:\n  - family\n---\nb")
    // Commitments: one open (no task), one assigned.
    v.Seed("commitments/open.md", "---\ntype: Commitment\ntitle: Open\nstatus: unresolved\npriority: high\ndue: 2026-07-01\ntask_assigned: ''\n---\nb")
    v.Seed("commitments/assigned.md", "---\ntype: Commitment\ntitle: Assigned\nstatus: unresolved\npriority: high\ndue: 2026-07-01\ntask_assigned: tasks/pending/urgent.md\n---\nb")
    // Topics: one fresh, one stale (>14 days).
    v.Seed("topics/active/fresh.md", "---\ntype: Topic\ntitle: Fresh\nstatus: active\nlast_updated: 2026-06-20\n---\nb")
    v.Seed("topics/active/stale.md", "---\ntype: Topic\ntitle: Stale\nstatus: active\nlast_updated: 2026-05-01\n---\nb")
    v

// A chat client that echoes a fixed prose string.
let private proseChat (text: string) = FakeChatClient([ Responses.final text ])

let private deps (v: FakeVault) (chat: IChatClient) : DigestDeps =
    { Vault = v :> IVault; Chat = chat; Model = "test-model"; Today = today }

[<Fact>]
let ``daily digest selects, counts, writes a dated note, and uses the LLM prose`` () =
    let v = seed ()
    let d = deps v (proseChat "BRIEFING TEXT")
    let r = Digest.generate d DigestParams.daily
    Assert.Equal("digests/2026-06-23-daily.md", r.Path)
    Assert.Equal(2, r.TaskCount)             // urgent + later; bad.md skipped
    Assert.Equal(1, r.EventCount)            // soon (in 7d); far excluded
    Assert.Equal(1, r.CommitmentCount)       // open; assigned excluded
    Assert.Equal(1, r.StaleTopicCount)       // stale; fresh excluded
    Assert.Contains("BRIEFING TEXT", v.Files.[r.Path])
    Assert.Contains("BRIEFING TEXT", r.Text)

[<Fact>]
let ``weekly digest widens the event window to 14 days`` () =
    let v = FakeVault()
    v.Seed("events/2026/07/day10.md", "---\ntype: Event\ntitle: Day10\nwhen: 2026-07-03T09:00:00+02:00\ncontext:\n  - family\n---\nb")  // 10 days out
    let d = deps v (proseChat "X")
    let daily = Digest.generate d DigestParams.daily
    Assert.Equal(0, daily.EventCount)        // outside 7-day window
    let weekly = Digest.generate d DigestParams.weekly
    Assert.Equal(1, weekly.EventCount)       // inside 14-day window

[<Fact>]
let ``stale threshold is 14 days`` () =
    let v = FakeVault()
    v.Seed("topics/active/edge13.md", "---\ntype: Topic\ntitle: E13\nstatus: active\nlast_updated: 2026-06-10\n---\nb")  // 13 days
    v.Seed("topics/active/edge15.md", "---\ntype: Topic\ntitle: E15\nstatus: active\nlast_updated: 2026-06-08\n---\nb")  // 15 days
    let d = deps v (proseChat "X")
    let r = Digest.generate d DigestParams.daily
    Assert.Equal(1, r.StaleTopicCount)       // only the 15-day topic is stale

[<Fact>]
let ``LLM failure falls back to a deterministic body`` () =
    let v = seed ()
    // FakeChatClient with an empty script throws on Chat (queue underflow) -> fallback path.
    let d = deps v (FakeChatClient([]))
    let r = Digest.generate d DigestParams.daily
    Assert.True(v.Files.ContainsKey(r.Path))
    Assert.Contains("Urgent", r.Text)        // fallback renders the selected task titles

[<Fact>]
let ``tied-score tasks are ordered by soonest due date ascending`` () =
    // Two pending tasks, same context (medical) → equal base score.
    // Due dates > 7 days away so neither gets a due-proximity modifier.
    // Sooner task has a LATER-alphabetically title ("Zzz") to ensure the tie-break
    // is on due date, not title; the buggy descending-title sort would put "Zzz" last.
    let v = FakeVault()
    v.Seed("tasks/pending/sooner.md", "---\ntype: Task\ntitle: Zzz\nstatus: pending\npriority: medium\ndue: 2026-08-01\ncontext:\n  - medical\n---\nb")
    v.Seed("tasks/pending/later.md", "---\ntype: Task\ntitle: Aaa\nstatus: pending\npriority: medium\ndue: 2026-09-01\ncontext:\n  - medical\n---\nb")
    // FakeChatClient([]) → queue underflow → fallback body used (deterministic rendering)
    let d = { Vault = v :> IVault; Chat = FakeChatClient([]) :> IChatClient; Model = "test-model"; Today = today }
    let r = Digest.generate d DigestParams.daily
    // Sooner due date ("Zzz", 2026-08-01) must appear before later due date ("Aaa", 2026-09-01)
    let idxZzz = r.Text.IndexOf("Zzz")
    let idxAaa = r.Text.IndexOf("Aaa")
    Assert.True(idxZzz >= 0, "Expected 'Zzz' in digest text")
    Assert.True(idxAaa >= 0, "Expected 'Aaa' in digest text")
    Assert.True(idxZzz < idxAaa, sprintf "Expected 'Zzz' (sooner due) before 'Aaa' (later due), but found positions %d vs %d" idxZzz idxAaa)
