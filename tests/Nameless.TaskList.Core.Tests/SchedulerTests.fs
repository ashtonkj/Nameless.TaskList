module Nameless.TaskList.Core.Tests.SchedulerTests

open System
open Nameless.TaskList.Core
open Xunit

[<Fact>]
let ``parseSpec reads a daily HH:mm`` () =
    Assert.Equal(Some(Scheduler.Daily(7, 0)), Scheduler.parseSpec "07:00")

[<Fact>]
let ``parseSpec reads a weekly Ddd HH:mm case-insensitively`` () =
    Assert.Equal(Some(Scheduler.Weekly(DayOfWeek.Monday, 7, 0)), Scheduler.parseSpec "mon 07:00")

[<Fact>]
let ``parseSpec rejects blank, garbage and out-of-range`` () =
    Assert.Equal(None, Scheduler.parseSpec "")
    Assert.Equal(None, Scheduler.parseSpec "   ")
    Assert.Equal(None, Scheduler.parseSpec null)
    Assert.Equal(None, Scheduler.parseSpec "garbage")
    Assert.Equal(None, Scheduler.parseSpec "25:00")
    Assert.Equal(None, Scheduler.parseSpec "Xyz 07:00")

[<Fact>]
let ``mostRecentOccurrence daily returns today when the time has passed`` () =
    let now = DateTime(2026, 6, 28, 9, 0, 0)
    Assert.Equal(DateTime(2026, 6, 28, 7, 0, 0), Scheduler.mostRecentOccurrence now (Scheduler.Daily(7, 0)))

[<Fact>]
let ``mostRecentOccurrence daily returns yesterday when the time has not passed`` () =
    let now = DateTime(2026, 6, 28, 6, 0, 0)
    Assert.Equal(DateTime(2026, 6, 27, 7, 0, 0), Scheduler.mostRecentOccurrence now (Scheduler.Daily(7, 0)))

[<Fact>]
let ``mostRecentOccurrence weekly on the day after the time returns today`` () =
    // 2026-06-29 is a Monday
    let now = DateTime(2026, 6, 29, 9, 0, 0)
    Assert.Equal(DateTime(2026, 6, 29, 7, 0, 0), Scheduler.mostRecentOccurrence now (Scheduler.Weekly(DayOfWeek.Monday, 7, 0)))

[<Fact>]
let ``mostRecentOccurrence weekly on the day before the time returns last week`` () =
    let now = DateTime(2026, 6, 29, 6, 0, 0)   // Monday, before 07:00
    Assert.Equal(DateTime(2026, 6, 22, 7, 0, 0), Scheduler.mostRecentOccurrence now (Scheduler.Weekly(DayOfWeek.Monday, 7, 0)))

[<Fact>]
let ``mostRecentOccurrence weekly on a different weekday returns the most recent matching day`` () =
    let now = DateTime(2026, 7, 1, 12, 0, 0)   // Wednesday
    Assert.Equal(DateTime(2026, 6, 29, 7, 0, 0), Scheduler.mostRecentOccurrence now (Scheduler.Weekly(DayOfWeek.Monday, 7, 0)))

let private stateOf (pairs: (string * DateTime) list) : SchedulerState =
    { LastRuns = System.Collections.Generic.Dictionary(dict pairs) }

let private daily name h m : Scheduler.ScheduledTask = { Name = name; Spec = Scheduler.Daily(h, m) }

[<Fact>]
let ``dueTasks includes a task never run whose slot has passed`` () =
    let now = DateTime(2026, 6, 28, 8, 0, 0)
    let due = Scheduler.dueTasks now [ daily "daily-digest" 7 0 ] (stateOf [])
    Assert.Equal<string list>([ "daily-digest" ], due |> List.map (fun t -> t.Name))

[<Fact>]
let ``dueTasks excludes a task already run this slot`` () =
    let now = DateTime(2026, 6, 28, 8, 0, 0)
    let state = stateOf [ "daily-digest", DateTime(2026, 6, 28, 7, 30, 0) ]   // ran after 07:00 today
    Assert.Empty(Scheduler.dueTasks now [ daily "daily-digest" 7 0 ] state)

[<Fact>]
let ``dueTasks fires once after multi-day downtime (catch-up, not repeated)`` () =
    let now = DateTime(2026, 6, 28, 8, 0, 0)
    let state = stateOf [ "daily-digest", DateTime(2026, 6, 25, 7, 0, 0) ]    // last ran 3 days ago
    let due = Scheduler.dueTasks now [ daily "daily-digest" 7 0 ] state
    Assert.Equal(1, List.length due)                                          // due once
    // after running, the same now is no longer due
    let after = Scheduler.tick now [ daily "daily-digest" 7 0 ] state (fun _ -> ())
    Assert.Empty(Scheduler.dueTasks now [ daily "daily-digest" 7 0 ] after)

[<Fact>]
let ``tick runs due tasks and advances only their last-run`` () =
    let now = DateTime(2026, 6, 28, 8, 0, 0)
    let tasks = [ daily "daily-digest" 7 0; daily "reindex" 9 0 ]   // reindex 09:00 not yet due at 08:00
    let ran = ResizeArray<string>()
    let after = Scheduler.tick now tasks (stateOf []) (fun t -> ran.Add t.Name)
    Assert.Equal<string list>([ "daily-digest" ], List.ofSeq ran)
    Assert.True(after.LastRuns.ContainsKey "daily-digest")
    Assert.False(after.LastRuns.ContainsKey "reindex")
    Assert.Equal(now, after.LastRuns.["daily-digest"])

[<Fact>]
let ``tick does not mutate the input state`` () =
    let now = DateTime(2026, 6, 28, 8, 0, 0)
    let input = stateOf []
    Scheduler.tick now [ daily "daily-digest" 7 0 ] input (fun _ -> ()) |> ignore
    Assert.Empty(input.LastRuns)   // original untouched
