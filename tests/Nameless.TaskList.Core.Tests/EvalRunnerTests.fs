module Nameless.TaskList.Core.Tests.EvalRunnerTests

open System.IO
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Tests.Fakes
open Nameless.TaskList.Eval
open Xunit

let private withDataset (f: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "ntl-runner-" + System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root, "classify")) |> ignore
    Directory.CreateDirectory(Path.Combine(root, "_worlds", "_base", "contexts")) |> ignore
    File.WriteAllText(Path.Combine(root, "_worlds", "_base", "contexts", "family.md"),
                      "---\ntype: Context\nname: family\n---\nFamily.")
    try f root
    finally (try Directory.Delete(root, true) with _ -> ())

[<Fact>]
let ``runCase scores a classify case end to end with a fake model`` () =
    withDataset (fun root ->
        File.WriteAllText(Path.Combine(root, "classify", "c1.json"),
            """{"id":"c1","step":"classify","world":"_base",
                "input":{"message":"thanks so much!"},
                "expected":{"noise":true}}""")
        let chat =
            FakeChatClient([ Responses.final
                """{"noise":true,"noise_reason":"gratitude","contexts":[],"intent":null,
                    "action_required":false,"urgency":"none","people_mentioned":[],
                    "entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}""" ])
        let embedder = FakeEmbedder(fun _ -> [| 1.0 |])
        let case = Dataset.load root [ "classify" ] |> List.head
        let r = Runner.runCase (chat :> IChatClient) (embedder :> IEmbedder) root 5 0.5 case
        Assert.Equal(1.0, r.Score, 3)
        Assert.Equal(Some(true, true), r.NoisePair))

/// Write a single generative case under <root>/<step>/c.json, script one model reply, run it.
let private runGen (root: string) (step: string) (inputJson: string) (expectedJson: string) (reply: string) : Scoring.CaseResult =
    Directory.CreateDirectory(Path.Combine(root, step)) |> ignore
    File.WriteAllText(Path.Combine(root, step, "c.json"),
        sprintf """{"id":"c","step":"%s","world":"_base","input":%s,"expected":%s}""" step inputJson expectedJson)
    let chat = FakeChatClient([ Responses.final reply ])
    let embedder = FakeEmbedder(fun _ -> [| 1.0 |])
    let case = Dataset.load root [ step ] |> List.head
    Runner.runCase (chat :> IChatClient) (embedder :> IEmbedder) root 5 0.5 case

[<Fact>]
let ``runCase scores a task-create case end to end`` () =
    withDataset (fun root ->
        let r =
            runGen root "task-create"
                """{"intent":"Book Ethan's flu vaccine before next Friday","message":"...","referenceDate":"2026-06-24","contexts":["medical"],"urgency":"medium"}"""
                """{"frontmatter":{"status":"pending","priority":"medium","context":["medical"],"due":"2026-07-03"},"titleMatches":"^Book\\b","bodyContains":["flu vaccine"]}"""
                "---\ntype: Task\ntitle: Book Ethan's flu vaccine\nstatus: pending\npriority: medium\ndue: 2026-07-03\ncontext: [medical]\npeople: []\n---\nBook the flu vaccine.\n"
        Assert.Equal(1.0, r.Score, 3))

[<Fact>]
let ``runCase scores an event-create case end to end`` () =
    withDataset (fun root ->
        let r =
            runGen root "event-create"
                """{"intent":"Dentist at 10am on 22 June","message":"...","referenceDate":"2026-06-18","contexts":["medical"],"urgency":"medium"}"""
                """{"frontmatter":{"all_day":false,"when":"2026-06-22T10:00:00+02:00"},"titleMatches":"[Dd]entist"}"""
                "---\ntype: Event\ntitle: Dentist appointment\nwhen: 2026-06-22T10:00:00+02:00\nall_day: false\ncontext: [medical]\n---\nDentist visit.\n"
        Assert.Equal(1.0, r.Score, 3))

[<Fact>]
let ``runCase scores a commitment-create case end to end`` () =
    withDataset (fun root ->
        let r =
            runGen root "commitment-create"
                """{"intent":"Return the signed form by 1 July","message":"...","referenceDate":"2026-06-24","contexts":["school"],"urgency":"medium"}"""
                """{"frontmatter":{"status":"unresolved","due":"2026-07-01"},"bodyContains":["form"]}"""
                "---\ntype: Commitment\ntitle: Return signed form\nstatus: unresolved\npriority: medium\ndue: 2026-07-01\ncontext: [school]\n---\nReturn the signed form.\n"
        Assert.Equal(1.0, r.Score, 3))

[<Fact>]
let ``runCase scores a note-create case end to end`` () =
    withDataset (fun root ->
        let r =
            runGen root "note-create"
                """{"intent":"Medical aid number MA-4471829","message":"...","referenceDate":"2026-06-24","contexts":["medical"],"urgency":"low"}"""
                """{"frontmatter":{"context":["medical"]},"bodyContains":["MA-4471829"]}"""
                "---\ntype: Note\ntitle: Medical aid details\ncontext: [medical]\ntags: []\n---\nMembership MA-4471829.\n"
        Assert.Equal(1.0, r.Score, 3))

[<Fact>]
let ``runCase scores a relationship-extract case end to end`` () =
    withDataset (fun root ->
        let r =
            runGen root "relationship-extract"
                """{"slugs":["sarah-ashford","ethan-ashford"],"message":"Sarah picked up her son Ethan."}"""
                """{"relationships":[{"from":"sarah-ashford","to":"ethan-ashford","relation":"parent-child"}]}"""
                """{"relationships":[{"from":"sarah-ashford","to":"ethan-ashford","relation":"parent-child","descriptor":"mother and son","confidence":"high"}]}"""
        Assert.Equal(1.0, r.Score, 3))

[<Fact>]
let ``runCase scores a task-match case against a seeded candidate`` () =
    withDataset (fun root ->
        // seed a candidate task into the _base world so matchTask has something to shortlist
        let worldDir = Path.Combine(root, "_worlds", "_base", "tasks", "pending")
        Directory.CreateDirectory worldDir |> ignore
        File.WriteAllText(Path.Combine(worldDir, "sign-up-ethan-for-swimming.md"),
                          "---\ntype: Task\ntitle: Sign up Ethan for swimming\nstatus: pending\n---\nRegister Ethan.\n")
        Directory.CreateDirectory(Path.Combine(root, "task-match")) |> ignore
        File.WriteAllText(Path.Combine(root, "task-match", "c.json"),
            """{"id":"c","step":"task-match","world":"_base","input":{"intent":"Register Ethan for swimming"},"expected":{"decision":"match","slug":"sign-up-ethan-for-swimming"}}""")
        let chat = FakeChatClient([ Responses.final """{"match":true,"topic_slug":"sign-up-ethan-for-swimming","confidence":0.9,"match_reason":"same","new_topic_title":null}""" ])
        let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |])
        let case = Dataset.load root [ "task-match" ] |> List.head
        let r = Runner.runCase (chat :> IChatClient) (embedder :> IEmbedder) root 5 0.5 case
        Assert.Equal(1.0, r.Score, 3))
