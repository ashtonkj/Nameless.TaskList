namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline

module BulkProcessor =

    // Public (returned to the HTTP layer): a private record would serialize to `{}`.
    type BulkProgress =
        { Total: int
          Processed: int
          Noise: int
          Skipped: int
          Errors: int
          Done: bool
          Error: string }

    /// True if any job in the set has not finished.
    let isRunning (progresses: BulkProgress seq) : bool =
        progresses |> Seq.exists (fun p -> not p.Done)

    /// Re-run `processOne` over every message since `since` (ascending), tallying outcomes
    /// and reporting progress after each. A per-message exception or LlmError/NotFound is
    /// counted as an error and does not abort the run.
    let runSince (messages: IMessageSource) (processOne: string -> string -> PipelineResult)
                 (since: System.DateTime) (chatJid: string option) (onProgress: BulkProgress -> unit) : BulkProgress =
        let msgs = messages.GetMessagesSince(chatJid, since)
        let mutable p =
            { Total = List.length msgs; Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Done = false; Error = "" }
        for m in msgs do
            (try
                match processOne m.Id m.ChatJid with
                | Processed _ -> p <- { p with Processed = p.Processed + 1 }
                | ProcessedNoise -> p <- { p with Noise = p.Noise + 1 }
                | Skipped -> p <- { p with Skipped = p.Skipped + 1 }
                | LlmError _ | NotFound -> p <- { p with Errors = p.Errors + 1 }
             with _ -> p <- { p with Errors = p.Errors + 1 })
            onProgress p
        let final = { p with Done = true }
        onProgress final
        final
