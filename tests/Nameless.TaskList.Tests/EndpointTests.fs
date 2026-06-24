module Nameless.TaskList.Tests.EndpointTests

open Nameless.TaskList
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline
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
    let result = BulkJobs.tryStart fake (fun _ _ -> Skipped) (System.DateTime(2026, 6, 1)) None
    let jobId =
        match result with
        | Ok id -> id
        | Error e -> failwith e

    let mutable progress = None
    for _ = 1 to 50 do
        System.Threading.Thread.Sleep(20)
        progress <- BulkJobs.get jobId

    let final = progress
    Assert.True(Option.isSome final, "Expected to find the job after polling")
    let p = Option.get final
    Assert.Equal("error", p.Status)
    Assert.True((p.Error.Length > 0 && p.Error.Contains("db down")), sprintf "Expected error containing 'db down', got: %s" p.Error)
