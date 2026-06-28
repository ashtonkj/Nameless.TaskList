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
