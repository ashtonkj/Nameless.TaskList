module Nameless.TaskList.Tests.EndpointTests

open Nameless.TaskList
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline
open Nameless.TaskList.BulkJobs
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Xunit

// Verifies the result-to-HTTP mapping (spec §8.2) without a live host.
// ASP.NET 10's IResult.ExecuteAsync requires RequestServices with logging + routing.
let private statusOf (result: PipelineResult) : int =
    let httpResult = ProcessMessageHandler.toHttp result
    let ctx = DefaultHttpContext()
    let services = ServiceCollection()
    services.AddLogging() |> ignore
    services.AddRouting() |> ignore
    ctx.RequestServices <- services.BuildServiceProvider()
    (httpResult.ExecuteAsync(ctx)).Wait()
    ctx.Response.StatusCode

[<Fact>]
let ``NotFound maps to 404`` () =
    Assert.Equal(404, statusOf NotFound)

[<Fact>]
let ``Skipped maps to 200`` () =
    Assert.Equal(200, statusOf Skipped)

[<Fact>]
let ``LlmError maps to 502`` () =
    Assert.Equal(502, statusOf (LlmError "bad json"))

[<Fact>]
let ``Processed maps to 200`` () =
    Assert.Equal(200, statusOf (Processed("topics/active/x.md", [ "tasks/pending/y.md" ])))

let private statusOfResult (r: IResult) : int =
    let ctx = DefaultHttpContext()
    let services = ServiceCollection()
    services.AddLogging() |> ignore
    services.AddRouting() |> ignore
    ctx.RequestServices <- services.BuildServiceProvider()
    (r.ExecuteAsync(ctx)).Wait()
    ctx.Response.StatusCode

[<Fact>]
let ``reindex summary maps to 200`` () =
    let summary : Nameless.TaskList.Core.Indexer.IndexSummary =
        { Tasks = 2; Topics = 1; Events = 0; Commitments = 0; Notes = 0; People = 0; Channels = 1; Skipped = 0 }
    Assert.Equal(200, statusOfResult (ReindexHandler.toHttp summary))

[<Fact>]
let ``digest result maps to 200`` () =
    let r : Nameless.TaskList.Core.Digest.DigestResult =
        { Path = "digests/2026-06-23-daily.md"; Text = "hi"
          TaskCount = 2; EventCount = 1; CommitmentCount = 0; StaleTopicCount = 1 }
    Assert.Equal(200, statusOfResult (DigestHandler.toHttp r))

[<Fact>]
let ``bulk start Ok maps to 202`` () =
    Assert.Equal(202, statusOfResult (BulkHandler.startToHttp (Ok "job123")))

[<Fact>]
let ``bulk start Error maps to 409`` () =
    Assert.Equal(409, statusOfResult (BulkHandler.startToHttp (Error "already running")))

[<Fact>]
let ``bulk progress Some maps to 200 and None to 404`` () =
    let p : Nameless.TaskList.Core.BulkProcessor.BulkJob =
        { JobId = "j1"; Since = System.DateTime(2026, 6, 1); ChatJid = ""
          StartedAt = System.DateTime(2026, 6, 1); Status = "done"
          Total = 3; Processed = 2; Noise = 0; Skipped = 1; Errors = 0; Error = "" }
    Assert.Equal(200, statusOfResult (BulkHandler.progressToHttp (Some p)))
    Assert.Equal(404, statusOfResult (BulkHandler.progressToHttp None))

open System.Threading

// In-memory IJobStore: Load returns the seed; Save records the latest snapshot.
type private FakeJobStore(seed: Nameless.TaskList.Core.BulkProcessor.BulkJob list) =
    let mutable saved = seed
    member _.Saved = saved
    interface Nameless.TaskList.Core.BulkProcessor.IJobStore with
        member _.Load() = seed
        member _.Save(js) = saved <- js

type private SinceSource(msgs: ChatMessage list) =
    interface IMessageSource with
        member _.GetMessage(_, _) = None
        member _.GetRecent(_, _, _) = []
        member _.GetMessagesSince(_, _) = msgs
        member _.GetMediaBytes(_, _) = None

let private testMsg (id: string) : ChatMessage =
    { Id = id; ChatJid = "jid"; ChatName = "c"; NormalizedChatName = "c"; IsGroup = false
      SenderId = "s"; SenderName = "s"; SenderPushName = null; SenderSavedName = null
      SenderBusinessName = null; IsFromMe = false; Content = "x"; MediaType = null
      FileName = null; AlbumId = null; AlbumIndex = None; Timestamp = System.DateTime(2026, 6, 1) }

let private pollUntil (predicate: unit -> bool) : bool =
    let mutable ok = false
    let mutable i = 0
    while not ok && i < 100 do
        ok <- predicate ()
        if not ok then Thread.Sleep(20)
        i <- i + 1
    ok

// Fake IMessageSource that throws on GetMessagesSince to test background job error handling.
type private FailingMessageSource() =
    interface IMessageSource with
        member _.GetMessage(_, _) = None
        member _.GetRecent(_, _, _) = []
        member _.GetMessagesSince(_, _) = failwith "db down"
        member _.GetMediaBytes(_, _) = None

[<Fact>]
let ``bulk job background failure is caught and stored`` () =
    let fake = FailingMessageSource() :> IMessageSource
    let reg = BulkJobRegistry(FakeJobStore([]), 20)
    let jobId = match reg.TryStart fake (fun _ _ -> Skipped) (System.DateTime(2026, 6, 1)) None with Ok i -> i | Error e -> failwith e
    Assert.True(pollUntil (fun () -> match reg.Get jobId with Some j -> j.Status = "error" | None -> false))
    let p = (reg.Get jobId).Value
    Assert.True(p.Error.Contains("db down"), sprintf "Expected error containing 'db down', got: %s" p.Error)

[<Fact>]
let ``registry rejects a second concurrent job`` () =
    let started = new ManualResetEventSlim(false)
    let release = new ManualResetEventSlim(false)
    let src = SinceSource([ testMsg "a"; testMsg "b" ]) :> IMessageSource
    let processOne _ _ = started.Set(); release.Wait(); Skipped
    let reg = BulkJobRegistry(FakeJobStore([]), 20)
    let first = reg.TryStart src processOne (System.DateTime(2026, 6, 1)) None
    Assert.True(started.Wait(1000))
    let second = reg.TryStart src processOne (System.DateTime(2026, 6, 1)) None
    match first with Ok _ -> () | Error e -> failwithf "expected Ok, got %s" e
    match second with Error _ -> () | Ok _ -> failwith "expected Error (already running)"
    release.Set()

[<Fact>]
let ``registry Cancel returns UnknownJob for an unknown id`` () =
    let reg = BulkJobRegistry(FakeJobStore([]), 20)
    Assert.Equal(UnknownJob, reg.Cancel "missing")

[<Fact>]
let ``registry Cancel stops a running job and reports cancelled`` () =
    let started = new ManualResetEventSlim(false)
    let release = new ManualResetEventSlim(false)
    let src = SinceSource([ testMsg "a"; testMsg "b"; testMsg "c" ]) :> IMessageSource
    let processOne _ _ = started.Set(); release.Wait(); Skipped
    let reg = BulkJobRegistry(FakeJobStore([]), 20)
    let id = match reg.TryStart src processOne (System.DateTime(2026, 6, 1)) None with Ok i -> i | Error e -> failwith e
    Assert.True(started.Wait(1000))
    Assert.Equal(Cancelled, reg.Cancel id)
    release.Set()
    Assert.True(pollUntil (fun () -> match reg.Get id with Some j -> j.Status = "cancelled" | None -> false))

[<Fact>]
let ``registry resumes an interrupted job from the store`` () =
    let seed : Nameless.TaskList.Core.BulkProcessor.BulkJob =
        { JobId = "old"; Since = System.DateTime(2026, 6, 1); ChatJid = ""
          StartedAt = System.DateTime(2026, 6, 1); Status = "running"
          Total = 0; Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Error = "" }
    let src = SinceSource([ testMsg "a"; testMsg "b" ]) :> IMessageSource
    let reg = BulkJobRegistry(FakeJobStore([ seed ]), 20)
    reg.Resume src (fun _ _ -> Skipped)
    Assert.True(pollUntil (fun () -> match reg.Get "old" with Some j -> j.Status = "done" | None -> false))
    let j = (reg.Get "old").Value
    Assert.Equal(2, j.Skipped)

[<Fact>]
let ``registry Resume marks older running jobs interrupted and completes the newest`` () =
    let older : Nameless.TaskList.Core.BulkProcessor.BulkJob =
        { JobId = "old1"; Since = System.DateTime(2026, 6, 1); ChatJid = ""
          StartedAt = System.DateTime(2026, 6, 1, 10, 0, 0); Status = "running"
          Total = 0; Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Error = "" }
    let newer : Nameless.TaskList.Core.BulkProcessor.BulkJob =
        { JobId = "new1"; Since = System.DateTime(2026, 6, 1); ChatJid = ""
          StartedAt = System.DateTime(2026, 6, 1, 12, 0, 0); Status = "running"
          Total = 0; Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Error = "" }
    let src = SinceSource([ testMsg "a"; testMsg "b" ]) :> IMessageSource
    let reg = BulkJobRegistry(FakeJobStore([ older; newer ]), 20)
    reg.Resume src (fun _ _ -> Skipped)
    // The older job (lower StartedAt) should be marked interrupted
    Assert.True(pollUntil (fun () -> match reg.Get "old1" with Some j -> j.Status = "interrupted" | None -> false))
    // The newer job (highest StartedAt) should reach a terminal status (done)
    Assert.True(pollUntil (fun () -> match reg.Get "new1" with Some j -> j.Status = "done" | None -> false))

[<Fact>]
let ``bulk cancel outcomes map to 200/404/409`` () =
    Assert.Equal(200, statusOfResult (BulkHandler.cancelToHttp Cancelled))
    Assert.Equal(404, statusOfResult (BulkHandler.cancelToHttp UnknownJob))
    Assert.Equal(409, statusOfResult (BulkHandler.cancelToHttp AlreadyTerminal))
