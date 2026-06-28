module Nameless.TaskList.Core.Tests.EvalWorldsTests

open System.IO
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Eval
open Xunit

let private datasetRoot =
    Path.Combine(Config.repoRoot, "eval", "dataset")

[<Fact>]
let ``base world exposes contexts to get-contexts shape`` () =
    let v = Worlds.load datasetRoot "_base"
    Assert.True(v.Exists "contexts/family.md")
    Assert.Contains("contexts/family.md", v.ListFiles "contexts")

[<Fact>]
let ``named world overlays base`` () =
    let v = Worlds.load datasetRoot "world-a"
    // base file still present
    Assert.True(v.Exists "contexts/family.md")
    // world-a person present and discoverable recursively
    Assert.Contains("people/medical/dr-naidoo.md", v.ListFilesRecursive "people")
