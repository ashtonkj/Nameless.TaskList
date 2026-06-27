namespace Nameless.TaskList

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline

/// Drives the WhatsApp pipeline from Postgres LISTEN/NOTIFY: a connection session (LISTEN →
/// catch-up → live) wrapped in a reconnect loop. Off unless WhatsApp:Listen:Enabled = "true".
type WhatsAppListenerService
    (listener: INotificationListener, cursorStore: IListenCursorStore, messages: IMessageSource,
     buildDeps: unit -> PipelineDeps, channel: string, reconnectSeconds: int,
     logger: ILogger<WhatsAppListenerService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            let deps = buildDeps ()
            let processOne (id: string) (jid: string) = Pipeline.processMessage deps id jid |> ignore
            while not stoppingToken.IsCancellationRequested do
                try
                    do! Task.Run((fun () ->
                            WhatsAppListener.runSession listener cursorStore messages processOne
                                channel (fun m -> logger.LogWarning("{Msg}", m)) stoppingToken),
                            stoppingToken)
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    logger.LogWarning(ex, "WhatsApp listener session ended; reconnecting")
                if not stoppingToken.IsCancellationRequested then
                    try do! Task.Delay(TimeSpan.FromSeconds(float reconnectSeconds), stoppingToken)
                    with :? OperationCanceledException -> ()
        } :> Task

    override _.Dispose() =
        match listener with
        | :? IDisposable as d -> d.Dispose()
        | _ -> ()
        base.Dispose()
