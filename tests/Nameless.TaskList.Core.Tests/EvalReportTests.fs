module Nameless.TaskList.Core.Tests.EvalReportTests

open Nameless.TaskList.Eval
open Xunit

let private result id step score noisePair : Scoring.CaseResult =
    { Id = id; Step = step; Tags = []; Score = score; Fields = []
      NoisePair = noisePair; ParseError = None }

[<Fact>]
let ``aggregate weights steps equally`` () =
    let results =
        [ result "a" "classify" 1.0 None
          result "b" "classify" 0.0 None
          result "c" "topic-match" 1.0 None ]
    let sc = Report.aggregate "m" results
    // classify mean = 0.5, topic-match mean = 1.0, overall = mean(0.5, 1.0) = 0.75
    Assert.Equal(0.75, sc.Overall, 3)

[<Fact>]
let ``noiseSummary computes precision and recall`` () =
    let results =
        [ result "tp" "classify" 1.0 (Some(true, true))
          result "fn" "classify" 0.0 (Some(true, false))
          result "fp" "classify" 0.0 (Some(false, true)) ]
    let p, r, fp, fn = Report.noiseSummary results
    Assert.Equal(0.5, p, 3)   // tp=1, fp=1
    Assert.Equal(0.5, r, 3)   // tp=1, fn=1
    Assert.Equal<string list>([ "fp" ], fp)
    Assert.Equal<string list>([ "fn" ], fn)

[<Fact>]
let ``scorecard JSON round-trips`` () =
    let sc = Report.aggregate "m" [ result "a" "classify" 1.0 None ]
    let back = Report.fromJson (Report.toJson sc)
    Assert.Equal(sc.Overall, back.Overall, 6)
    Assert.Equal(sc.Model, back.Model)
    Assert.Equal<(string * float * int) list>(sc.Steps, back.Steps)
    Assert.Equal<(string * string * float) list>(sc.Cases, back.Cases)

[<Fact>]
let ``markdown shows baseline delta`` () =
    let cur = Report.aggregate "m" [ result "a" "classify" 1.0 None ]
    let baseline = { cur with Overall = 0.8 }
    let md = Report.toMarkdown cur [ result "a" "classify" 1.0 None ] (Some baseline)
    Assert.Contains("+0.200", md)
