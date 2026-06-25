namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline

module BulkProcessor =

    /// Durable record for a bulk run: identity + params + live progress + status.
    /// Public + CLIMutable so System.Text.Json round-trips it both ways (a private
    /// record serializes to `{}`).
    [<CLIMutable>]
    type BulkJob =
        { JobId: string
          Since: System.DateTime
          ChatJid: string          // "" = all chats
          StartedAt: System.DateTime
          Status: string           // running | done | cancelled | interrupted | error
          Total: int
          Processed: int
          Noise: int
          Skipped: int
          Errors: int
          Error: string }

    /// True if any job is still running.
    let isRunning (jobs: BulkJob seq) : bool =
        jobs |> Seq.exists (fun j -> j.Status = "running")

    /// Retain every running job plus the most-recent `keep` non-running jobs (by StartedAt).
    let prune (keep: int) (jobs: BulkJob list) : BulkJob list =
        let running, finished = jobs |> List.partition (fun j -> j.Status = "running")
        let keptFinished =
            finished
            |> List.sortByDescending (fun j -> j.StartedAt)
            |> List.truncate (max 0 keep)
        running @ keptFinished

    /// Re-run `processOne` over every message since `job.Since` (ascending), tallying
    /// outcomes and reporting progress after each. A per-message exception or
    /// LlmError/NotFound is counted as an error and does not abort the run. The loop
    /// stops between messages when `token` is cancelled, finishing with status
    /// "cancelled"; otherwise it finishes "done".
    let runSince (messages: IMessageSource) (processOne: string -> string -> PipelineResult)
                 (job: BulkJob) (token: System.Threading.CancellationToken)
                 (onProgress: BulkJob -> unit) : BulkJob =
        let chatJid = if System.String.IsNullOrWhiteSpace job.ChatJid then None else Some job.ChatJid
        let msgs = messages.GetMessagesSince(chatJid, job.Since)
        let mutable j = { job with Total = List.length msgs; Status = "running"
                                   Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Error = "" }
        let mutable rest = msgs
        while not (List.isEmpty rest) && not token.IsCancellationRequested do
            let m = List.head rest
            rest <- List.tail rest
            (try
                match processOne m.Id m.ChatJid with
                | Processed _ -> j <- { j with Processed = j.Processed + 1 }
                | ProcessedNoise -> j <- { j with Noise = j.Noise + 1 }
                | Skipped -> j <- { j with Skipped = j.Skipped + 1 }
                | LlmError _ -> j <- { j with Errors = j.Errors + 1 }
                | NotFound ->
                    eprintfn "[bulk-error] msg=%s chat=%s: NotFound (message missing from source)" m.Id m.ChatJid
                    j <- { j with Errors = j.Errors + 1 }
             with ex ->
                eprintfn "[bulk-error] msg=%s chat=%s: unhandled %s: %s" m.Id m.ChatJid (ex.GetType().Name) ex.Message
                j <- { j with Errors = j.Errors + 1 })
            onProgress j
        let final = { j with Status = (if List.isEmpty rest then "done" else "cancelled") }
        onProgress final
        final

    /// Persists the set of bulk jobs. Save rewrites the whole set; Load is best-effort.
    type IJobStore =
        abstract member Save : jobs: BulkJob list -> unit
        abstract member Load : unit -> BulkJob list
