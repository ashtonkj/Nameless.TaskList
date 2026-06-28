module Nameless.TaskList.Core.Tests.EvalDatasetTests

open System.IO
open System.Text.Json
open Nameless.TaskList.Eval
open Xunit

let private withTempDataset (f: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "ntl-eval-" + System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root, "classify")) |> ignore
    try f root
    finally (try Directory.Delete(root, true) with _ -> ())

[<Fact>]
let ``load reads classify cases with defaults`` () =
    withTempDataset (fun root ->
        let json =
            """{"id":"c1","step":"classify","tags":["noise"],"world":"world-a",
                "input":{"message":"thanks!","referenceDate":"2026-06-24"},
                "expected":{"noise":true}}"""
        File.WriteAllText(Path.Combine(root, "classify", "c1.json"), json)
        let cases = Dataset.load root [ "classify" ]
        let c = Assert.Single(cases)
        Assert.Equal("c1", c.Id)
        Assert.Equal("classify", c.Step)
        Assert.Equal("world-a", c.World)
        Assert.Equal("thanks!", c.Input.GetProperty("message").GetString())
        Assert.True(c.Expected.GetProperty("noise").GetBoolean()))

[<Fact>]
let ``world defaults to _base when absent`` () =
    withTempDataset (fun root ->
        File.WriteAllText(Path.Combine(root, "classify", "c2.json"),
            """{"id":"c2","step":"classify","input":{"message":"x"},"expected":{"noise":false}}""")
        let c = Dataset.load root [ "classify" ] |> List.head
        Assert.Equal("_base", c.World))
