module Nameless.TaskList.Core.Tests.EvalDatasetIntegrityTests

open System.IO
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Eval
open Xunit

let private datasetRoot = Path.Combine(Config.repoRoot, "eval", "dataset")
let private knownSteps = set [ "classify"; "topic-match" ]

[<Fact>]
let ``every committed case parses and names a known step`` () =
    let cases = Dataset.load datasetRoot []
    Assert.NotEmpty(cases)
    for c in cases do
        Assert.True(knownSteps.Contains c.Step, sprintf "%s: unknown step '%s'" c.Id c.Step)

[<Fact>]
let ``every case world loads and is non-empty`` () =
    for c in Dataset.load datasetRoot [] do
        let v = Worlds.load datasetRoot c.World
        // _base always provides contexts; a named world must too.
        Assert.NotEmpty((v.ListFilesRecursive "contexts"))

[<Fact>]
let ``base world exposes all six contexts`` () =
    let v = Worlds.load datasetRoot "_base"
    for ctx in [ "family"; "medical"; "school"; "finance"; "professional"; "personal-kb" ] do
        Assert.True(v.Exists(sprintf "contexts/%s.md" ctx), sprintf "missing context %s" ctx)
