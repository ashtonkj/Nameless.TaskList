module Nameless.TaskList.Core.Tests.EvalArgsTests

open Nameless.TaskList.Eval
open Xunit

[<Fact>]
let ``parse reads model, steps, threshold`` () =
    match Args.parse [| "--model"; "granite4.1:8b"; "--steps"; "classify, topic-match"; "--threshold"; "0.9" |] with
    | Ok o ->
        Assert.Equal(Some "granite4.1:8b", o.Model)
        Assert.Equal<string list>([ "classify"; "topic-match" ], o.Steps)
        Assert.Equal(0.9, o.Threshold, 3)
    | Error e -> failwithf "expected Ok, got %s" e

[<Fact>]
let ``parse defaults threshold to 0.85 and steps to empty`` () =
    match Args.parse [||] with
    | Ok o -> Assert.Equal(0.85, o.Threshold, 3); Assert.Empty(o.Steps)
    | Error e -> failwithf "expected Ok, got %s" e

[<Fact>]
let ``parse rejects unknown flag`` () =
    match Args.parse [| "--frobnicate" |] with
    | Ok _ -> failwith "expected Error"
    | Error e -> Assert.Contains("frobnicate", e)
