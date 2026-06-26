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

open System
open Nameless.TaskList.Core.KnowledgeBase

let private task (context: string array) (due: string) (status: string) : Task =
    { Type = "Task"; Title = "T"; Description = "d"; Status = status; Priority = "medium"
      Due = due; Context = context; People = [||]; Topic = ""; SourceMessage = "" }

let private today = DateTime(2026, 6, 23)

[<Fact>]
let ``score uses the max context weight`` () =
    let t = task [| "family"; "medical" |] "" "pending"
    Assert.Equal(10, Scoring.scoreTask Weights.defaults today t)   // medical 10 > family 7, no due

[<Fact>]
let ``unknown and empty contexts contribute zero base`` () =
    Assert.Equal(0, Scoring.scoreTask Weights.defaults today (task [| "unknown" |] "" "pending"))
    Assert.Equal(0, Scoring.scoreTask Weights.defaults today (task [||] "" "pending"))

[<Fact>]
let ``due within 2 days adds the 2-day modifier only`` () =
    let t = task [| "family" |] "2026-06-24" "pending"   // tomorrow
    Assert.Equal(7 + 5, Scoring.scoreTask Weights.defaults today t)

[<Fact>]
let ``due within 7 days adds the 7-day modifier`` () =
    let t = task [| "family" |] "2026-06-28" "pending"   // 5 days out
    Assert.Equal(7 + 3, Scoring.scoreTask Weights.defaults today t)

[<Fact>]
let ``due far away or blank adds no due modifier`` () =
    Assert.Equal(7, Scoring.scoreTask Weights.defaults today (task [| "family" |] "2026-09-01" "pending"))
    Assert.Equal(7, Scoring.scoreTask Weights.defaults today (task [| "family" |] "" "pending"))

[<Fact>]
let ``past due counts as within 2 days`` () =
    let t = task [| "family" |] "2026-06-01" "pending"   // overdue
    Assert.Equal(7 + 5, Scoring.scoreTask Weights.defaults today t)

[<Fact>]
let ``blocked status adds the negative modifier`` () =
    let t = task [| "family" |] "" "blocked"
    Assert.Equal(7 - 2, Scoring.scoreTask Weights.defaults today t)

[<Fact>]
let ``parse handles the verbatim DESIGN priority-weights block`` () =
    let md = """
## Context weights (higher = more urgent)
medical:      10
finance:      10
school:        9
family:        7
professional:  5
personal-kb:   2

## Modifier rules
+3  task has explicit `due` date within 7 days
+5  task has explicit `due` date within 2 days
+5  commitment with no task_assigned and due within 7 days
+2  task blocks another task
+1  task involves an external person (requires coordination)
-2  task.status == "blocked" (deprioritise until unblocked)
"""
    let w = Weights.parse md
    Assert.Equal(10, w.ContextWeights.["medical"])
    Assert.Equal(10, w.ContextWeights.["finance"])
    Assert.Equal(9, w.ContextWeights.["school"])
    Assert.Equal(7, w.ContextWeights.["family"])
    Assert.Equal(5, w.ContextWeights.["professional"])
    Assert.Equal(2, w.ContextWeights.["personal-kb"])
    Assert.Equal(3, w.DueWithin7)           // +3 line comes before +5 line
    Assert.Equal(5, w.DueWithin2)
    Assert.Equal(5, w.UnassignedCommitmentDueWithin7)
    Assert.Equal(-2, w.Blocked)
