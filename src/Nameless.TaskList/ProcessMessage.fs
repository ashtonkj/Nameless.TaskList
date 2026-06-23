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
