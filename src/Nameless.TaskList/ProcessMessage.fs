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

    type CancelOutcome =
        | Cancelled
        | UnknownJob
        | AlreadyTerminal

    /// Injectable singleton owning live job state, cancellation tokens, persistence,
    /// pruning, and startup resume. The in-memory dictionary updates on every progress;
    /// the store is written at start, on each terminal transition, and on resume.
    type BulkJobRegistry(store: IJobStore, retain: int) =
        let liveJobs = ConcurrentDictionary<string, BulkJob>()
        let tokens = ConcurrentDictionary<string, System.Threading.CancellationTokenSource>()
        let regGate = obj ()

        do for j in store.Load() do liveJobs.[j.JobId] <- j

        let persist () = store.Save(liveJobs.Values |> List.ofSeq)

        let pruneAndPersist () =
            lock regGate (fun () ->
                let kept = prune retain (liveJobs.Values |> List.ofSeq)
                let keptIds = kept |> List.map (fun j -> j.JobId) |> Set.ofList
                for id in (liveJobs.Keys |> List.ofSeq) do
                    if not (Set.contains id keptIds) then liveJobs.TryRemove id |> ignore
                persist ())

        let runJob (messages: IMessageSource) (processOne: string -> string -> PipelineResult) (job: BulkJob) =
            let cts = new System.Threading.CancellationTokenSource()
            tokens.[job.JobId] <- cts
            System.Threading.Tasks.Task.Run(fun () ->
                (try runSince messages processOne job cts.Token (fun p -> liveJobs.[job.JobId] <- p) |> ignore
                 with ex -> liveJobs.[job.JobId] <- { liveJobs.[job.JobId] with Status = "error"; Error = ex.Message })
                tokens.TryRemove job.JobId |> ignore
                pruneAndPersist ()) |> ignore

        member _.TryStart (messages: IMessageSource) (processOne: string -> string -> PipelineResult)
                          (since: System.DateTime) (chatJid: string option) : Result<string, string> =
            let started =
                lock regGate (fun () ->
                    if isRunning liveJobs.Values then None
                    else
                        let jobId = System.Guid.NewGuid().ToString("N")
                        let job =
                            { JobId = jobId; Since = since; ChatJid = defaultArg chatJid ""
                              StartedAt = System.DateTime.UtcNow; Status = "running"
                              Total = 0; Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Error = "" }
                        liveJobs.[jobId] <- job
                        Some job)
            match started with
            | None -> Error "a bulk job is already running"
            | Some job ->
                pruneAndPersist ()
                runJob messages processOne job
                Ok job.JobId

        member _.Get (jobId: string) : BulkJob option =
            match liveJobs.TryGetValue jobId with
            | true, j -> Some j
            | _ -> None

        member _.Cancel (jobId: string) : CancelOutcome =
            match liveJobs.TryGetValue jobId with
            | false, _ -> UnknownJob
            | true, j when j.Status <> "running" -> AlreadyTerminal
            | true, _ ->
                match tokens.TryGetValue jobId with
                | true, cts -> cts.Cancel(); Cancelled
                | _ -> AlreadyTerminal

        member _.Resume (messages: IMessageSource) (processOne: string -> string -> PipelineResult) : unit =
            let interrupted =
                liveJobs.Values
                |> Seq.filter (fun j -> j.Status = "running")
                |> Seq.sortByDescending (fun j -> j.StartedAt)
                |> List.ofSeq
            match interrupted with
            | [] -> ()
            | latest :: rest ->
                lock regGate (fun () ->
                    for j in rest do liveJobs.[j.JobId] <- { j with Status = "interrupted" }
                    persist ())
                runJob messages processOne latest

module BulkHandler =
    let startToHttp (r: Result<string, string>) : IResult =
        match r with
        | Ok jobId -> Results.Json({| jobId = jobId |}, statusCode = 202)
        | Error msg -> Results.Json({| error = msg |}, statusCode = 409)

    let progressToHttp (p: Nameless.TaskList.Core.BulkProcessor.BulkJob option) : IResult =
        match p with
        | Some prog -> Results.Ok(box prog)
        | None -> Results.NotFound()

    let cancelToHttp (o: BulkJobs.CancelOutcome) : IResult =
        match o with
        | BulkJobs.Cancelled -> Results.Ok(box {| cancelled = true |})
        | BulkJobs.UnknownJob -> Results.NotFound()
        | BulkJobs.AlreadyTerminal -> Results.Json({| error = "job is not running" |}, statusCode = 409)
