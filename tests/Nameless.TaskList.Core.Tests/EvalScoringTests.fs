module Nameless.TaskList.Core.Tests.EvalScoringTests

open System.Text.Json
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Prompts
open Nameless.TaskList.Eval
open Xunit

let private caseFrom (expectedJson: string) : Dataset.Case =
    let doc = JsonDocument.Parse(sprintf """{"id":"t","step":"classify","expected":%s}""" expectedJson)
    let root = doc.RootElement.Clone()
    { Id = "t"; Step = "classify"; Tags = []; World = "_base"
      Input = root; Expected = root.GetProperty("expected"); SourcePath = "" }

let private classification noise contexts tasks events people : Classification =
    { Noise = noise; NoiseReason = ""; Contexts = contexts; Intent = ""; ActionRequired = false
      Urgency = "medium"; PeopleMentioned = people
      Entities = { Tasks = tasks; Events = events; Commitments = [||]; Notes = [||] } }

[<Fact>]
let ``setF1 rewards exact set, punishes spurious`` () =
    Assert.Equal(1.0, Scoring.setF1 [] [], 3)
    Assert.Equal(0.0, Scoring.setF1 [] [ "x" ], 3)
    Assert.Equal(1.0, Scoring.setF1 [ "school" ] [ "School" ], 3)   // normalised
    Assert.Equal(1.0, Scoring.setF1 [ "*picnic*" ] [ "Friday class picnic" ], 3)  // glob

[<Fact>]
let ``scoreClassify perfect case scores 1`` () =
    let case = caseFrom """{"noise":false,"contexts":["school"],"tasks":[],"events":["*picnic*"],"people":[]}"""
    let c = classification false [| "school" |] [||] [| "Friday class picnic" |] [||]
    let r = Scoring.scoreClassify case (Ok c)
    Assert.Equal(1.0, r.Score, 3)
    Assert.Equal(Some(false, false), r.NoisePair)

[<Fact>]
let ``scoreClassify penalises a missed noise call`` () =
    let case = caseFrom """{"noise":true}"""
    let c = classification false [||] [||] [||] [||]
    let r = Scoring.scoreClassify case (Ok c)
    Assert.Equal(0.0, r.Score, 3)
    Assert.Equal(Some(true, false), r.NoisePair)

[<Fact>]
let ``scoreClassify parse error scores 0 and records noise pair`` () =
    let case = caseFrom """{"noise":false}"""
    let r = Scoring.scoreClassify case (Error "bad json")
    Assert.Equal(0.0, r.Score, 3)
    Assert.True(r.ParseError.IsSome)

[<Fact>]
let ``scoreTopic match requires correct slug`` () =
    let mkCase j : Dataset.Case =
        let doc = JsonDocument.Parse(sprintf """{"id":"t","step":"topic-match","expected":%s}""" j)
        let root = doc.RootElement.Clone()
        { Id = "t"; Step = "topic-match"; Tags = []; World = "_base"
          Input = root; Expected = root.GetProperty("expected"); SourcePath = "" }
    let case = mkCase """{"decision":"match","slug":"gate-fault"}"""
    Assert.Equal(1.0, (Scoring.scoreTopic case (Ok (Steps.MatchExisting "gate-fault"))).Score, 3)
    Assert.Equal(0.0, (Scoring.scoreTopic case (Ok (Steps.MatchExisting "other"))).Score, 3)
    Assert.Equal(0.0, (Scoring.scoreTopic case (Ok (Steps.CreateTopic "X"))).Score, 3)

[<Fact>]
let ``setF1 never exceeds 1.0 when expected patterns overlap one actual`` () =
    // Two expected globs both match the single actual entry; precision must clamp to <= 1.0.
    let s = Scoring.setF1 [ "*gate*"; "*fault*" ] [ "the gate fault" ]
    Assert.True(s <= 1.0, sprintf "expected <= 1.0, got %f" s)

open Nameless.TaskList.Core.KnowledgeBase

let private genCase (step: string) (expectedJson: string) : Dataset.Case =
    let doc = JsonDocument.Parse(sprintf """{"id":"g","step":"%s","expected":%s}""" step expectedJson)
    let root = doc.RootElement.Clone()
    { Id = "g"; Step = step; Tags = []; World = "_base"
      Input = root; Expected = root.GetProperty("expected"); SourcePath = "" }

let private taskOutcome status priority due title body : Steps.EntityOutcome<Task> =
    { Record = { Type = "Task"; Title = title; Description = ""; Status = status; Priority = priority
                 Due = due; Context = [| "medical" |]; People = [||]; Topic = ""; SourceMessage = "" }
      Body = body }

[<Fact>]
let ``scoreTask perfect generation scores 1`` () =
    let case = genCase "task-create" """{"frontmatter":{"status":"pending","priority":"medium","context":["medical"],"due":"2026-07-03"},"titleMatches":"^Book\\b","bodyContains":["flu vaccine"]}"""
    let r = Scoring.scoreTask case (Ok (taskOutcome "pending" "medium" "2026-07-03" "Book flu vaccine" "Book the flu vaccine soon"))
    Assert.Equal(1.0, r.Score, 3)

[<Fact>]
let ``scoreTask penalises wrong due and bad title`` () =
    let case = genCase "task-create" """{"frontmatter":{"status":"pending","due":"2026-07-03"},"titleMatches":"^Book\\b"}"""
    // status right (1), due wrong (0), title wrong (0) -> mean 1/3
    let r = Scoring.scoreTask case (Ok (taskOutcome "pending" "low" "2026-07-10" "Maybe do the thing" ""))
    Assert.Equal(1.0/3.0, r.Score, 3)

[<Fact>]
let ``scoreTask generation error scores 0`` () =
    let case = genCase "task-create" """{"frontmatter":{"status":"pending"}}"""
    let r = Scoring.scoreTask case (Error "agent exceeded iterations")
    Assert.Equal(0.0, r.Score, 3)
    Assert.True(r.ParseError.IsSome)

[<Fact>]
let ``scoreEvent matches when by instant and all_day`` () =
    let case = genCase "event-create" """{"frontmatter":{"all_day":false,"when":"2026-06-22T10:00:00+02:00"}}"""
    let ev : Steps.EntityOutcome<Event> =
        { Record = { Type = "Event"; Title = "Meeting"; Description = ""; When = "2026-06-22T10:00:00+02:00"; AllDay = false
                     Context = [||]; Location = ""; People = [||]; Topic = ""; TasksLinked = [||]; ReminderDaysBefore = 3 }
          Body = "" }
    Assert.Equal(1.0, (Scoring.scoreEvent case (Ok ev)).Score, 3)
