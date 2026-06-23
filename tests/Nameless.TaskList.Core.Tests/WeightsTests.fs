module Nameless.TaskList.Core.Tests.WeightsTests

open Nameless.TaskList.Core
open Xunit

[<Fact>]
let ``defaults match DESIGN section 4.12`` () =
    let w = Weights.defaults
    Assert.Equal(10, w.ContextWeights.["medical"])
    Assert.Equal(9, w.ContextWeights.["school"])
    Assert.Equal(2, w.ContextWeights.["personal-kb"])
    Assert.Equal(3, w.DueWithin7)
    Assert.Equal(5, w.DueWithin2)
    Assert.Equal(-2, w.Blocked)

[<Fact>]
let ``parse reads context weights and modifiers from the markdown`` () =
    let md =
        "## Context weights\nmedical:      8\nfamily:        4\n\n" +
        "## Modifier rules\n+4  task has explicit due date within 7 days\n" +
        "+9  task has explicit due date within 2 days\n" +
        "-3  task.status == \"blocked\"\n"
    let w = Weights.parse md
    Assert.Equal(8, w.ContextWeights.["medical"])
    Assert.Equal(4, w.ContextWeights.["family"])
    Assert.Equal(4, w.DueWithin7)
    Assert.Equal(9, w.DueWithin2)
    Assert.Equal(-3, w.Blocked)

[<Fact>]
let ``parse falls back to defaults for missing entries`` () =
    let w = Weights.parse "## Context weights\nmedical: 11\n"
    Assert.Equal(11, w.ContextWeights.["medical"])
    Assert.Equal(7, w.ContextWeights.["family"])   // untouched default
    Assert.Equal(3, w.DueWithin7)                   // untouched default

[<Fact>]
let ``parse returns defaults on garbage`` () =
    let w = Weights.parse "this is not a weights file"
    Assert.Equal(Weights.defaults.DueWithin2, w.DueWithin2)
    Assert.Equal(Weights.defaults.ContextWeights.["finance"], w.ContextWeights.["finance"])
