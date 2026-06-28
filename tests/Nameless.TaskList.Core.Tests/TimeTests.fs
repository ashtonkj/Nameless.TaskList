module Nameless.TaskList.Core.Tests.TimeTests

open System
open Nameless.TaskList.Core.KnowledgeBase
open Xunit

[<Fact>]
let ``iso emits the given offset regardless of DateTime.Kind`` () =
    let dt = DateTime(2026, 6, 24, 9, 30, 0)
    Assert.Equal("2026-06-24T09:30:00+02:00", Time.iso (TimeSpan.FromHours 2.0) dt)
    Assert.Equal("2026-06-24T09:30:00+05:30", Time.iso (TimeSpan.FromHours 5.5) dt)
    // A Local-kind input must not throw and must still use the supplied offset.
    let local = DateTime.SpecifyKind(dt, DateTimeKind.Local)
    Assert.Equal("2026-06-24T09:30:00+02:00", Time.iso (TimeSpan.FromHours 2.0) local)
    // A Utc-kind input likewise.
    let utc = DateTime.SpecifyKind(dt, DateTimeKind.Utc)
    Assert.Equal("2026-06-24T09:30:00+02:00", Time.iso (TimeSpan.FromHours 2.0) utc)

[<Fact>]
let ``now is at the requested offset`` () =
    Assert.Equal(TimeSpan.FromHours 2.0, (Time.now (TimeSpan.FromHours 2.0)).Offset)
    Assert.Equal(TimeSpan.FromHours -5.0, (Time.now (TimeSpan.FromHours -5.0)).Offset)
