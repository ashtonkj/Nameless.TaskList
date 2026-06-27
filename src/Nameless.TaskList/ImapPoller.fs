namespace Nameless.TaskList

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Core.Pipeline

/// Polls IMAP on an interval and drives each new mail through the pipeline.
type ImapPollerService
    (mailbox: IMailbox, cursorStore: IEmailCursorStore, source: ImapMessageSource,
     buildEmailDeps: ImapMessageSource -> PipelineDeps, folder: string, pollSeconds: int,
     initialBackfill: uint32, logger: ILogger<ImapPollerService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            let deps = buildEmailDeps source
            while not stoppingToken.IsCancellationRequested do
                try
                    let stored = cursorStore.Load()
                    let mails, next = EmailPoller.fetch mailbox stored folder initialBackfill
                    for cm in mails do
                        source.Put cm
                        Pipeline.processMessage deps cm.Id cm.ChatJid |> ignore
                    cursorStore.Save next
                    if not (List.isEmpty mails) then
                        logger.LogInformation("IMAP poll processed {Count} message(s)", List.length mails)
                with ex ->
                    logger.LogWarning(ex, "IMAP poll failed; will retry next interval")
                try
                    do! Task.Delay(TimeSpan.FromSeconds(float pollSeconds), stoppingToken)
                with :? OperationCanceledException -> ()
        } :> Task
