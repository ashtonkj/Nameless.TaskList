namespace Nameless.TaskList

open Microsoft.AspNetCore.Http
open Nameless.TaskList.Core.Pipeline

[<CLIMutable>]
type ProcessMessageRequest = { Id: string; ChatJid: string }

module ProcessMessageHandler =
    /// Maps a pipeline result to an HTTP result per spec §8.2.
    let toHttp (result: PipelineResult) : IResult =
        match result with
        | NotFound -> Results.NotFound()
        | Skipped -> Results.Ok(box {| skipped = true |})
        | ProcessedNoise -> Results.Ok(box {| noise = true |})
        | Processed(topic, tasks) -> Results.Ok(box {| topic = topic; tasks = tasks |})
        | LlmError msg -> Results.Json({| error = msg |}, statusCode = 502)

module ReindexHandler =
    /// Maps an index summary to a 200 response.
    let toHttp (summary: Nameless.TaskList.Core.Indexer.IndexSummary) : IResult =
        Results.Ok(box summary)

module DigestHandler =
    /// Maps a digest result to a 200 response with its text + counts.
    let toHttp (r: Nameless.TaskList.Core.Digest.DigestResult) : IResult =
        Results.Ok(box {| path = r.Path; text = r.Text
                          taskCount = r.TaskCount; eventCount = r.EventCount
                          commitmentCount = r.CommitmentCount; staleTopicCount = r.StaleTopicCount |})

[<CLIMutable>]
type ProcessSinceRequest = { Since: string; ChatJid: string }

module BulkJobs =
    open System.Collections.Concurrent
    open Nameless.TaskList.Core.Ports
    open Nameless.TaskList.Core.Pipeline
    open Nameless.TaskList.Core.BulkProcessor

    let private jobs = ConcurrentDictionary<string, BulkJob>()
    let private gate = obj ()

    /// Start a background bulk run, unless one is already running. Returns the new job id.
    let tryStart (messages: IMessageSource) (processOne: string -> string -> PipelineResult)
                 (since: System.DateTime) (chatJid: string option) : Result<string, string> =
        let started =
            lock gate (fun () ->
                if isRunning jobs.Values then None
                else
                    let jobId = System.Guid.NewGuid().ToString("N")
                    let job =
                        { JobId = jobId; Since = since; ChatJid = defaultArg chatJid ""
                          StartedAt = System.DateTime.UtcNow; Status = "running"
                          Total = 0; Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Error = "" }
                    jobs.[jobId] <- job
                    Some job)
        match started with
        | None -> Error "a bulk job is already running"
        | Some job ->
            System.Threading.Tasks.Task.Run(fun () ->
                try runSince messages processOne job System.Threading.CancellationToken.None (fun p -> jobs.[job.JobId] <- p) |> ignore
                with ex -> jobs.[job.JobId] <- { jobs.[job.JobId] with Status = "error"; Error = ex.Message }) |> ignore
            Ok job.JobId

    let get (jobId: string) : BulkJob option =
        match jobs.TryGetValue jobId with
        | true, p -> Some p
        | _ -> None

module BulkHandler =
    let startToHttp (r: Result<string, string>) : IResult =
        match r with
        | Ok jobId -> Results.Json({| jobId = jobId |}, statusCode = 202)
        | Error msg -> Results.Json({| error = msg |}, statusCode = 409)

    let progressToHttp (p: Nameless.TaskList.Core.BulkProcessor.BulkJob option) : IResult =
        match p with
        | Some prog -> Results.Ok(box prog)
        | None -> Results.NotFound()
