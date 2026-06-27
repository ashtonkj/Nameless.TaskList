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
