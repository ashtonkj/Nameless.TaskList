namespace Nameless.TaskList

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports

/// The digest/reindex invocations shared by the HTTP endpoints and the in-app scheduler,
/// so a manual run and a scheduled run execute identical code reading the same config.
module MaintenanceTasks =

    let private topicCfg (cfg: IConfiguration) : Indexer.TopicSweepConfig =
        { ResolvedArchiveAfterDays = (match Int32.TryParse(cfg.["Topics:ResolvedArchiveAfterDays"]) with | true, n -> n | _ -> 14)
          DormantArchiveAfterDays = (match Int32.TryParse(cfg.["Topics:DormantArchiveAfterDays"]) with | true, n -> n | _ -> 90) }

    let kbOffset (cfg: IConfiguration) : System.TimeSpan =
        match System.Double.TryParse(cfg.["Vault:UtcOffsetHours"]) with
        | true, h -> System.TimeSpan.FromHours h
        | _ -> System.TimeSpan.FromHours 2.0

    let reindex (cfg: IConfiguration) (vault: IVault) : Indexer.IndexSummary =
        Indexer.regenerate vault (topicCfg cfg) (KnowledgeBase.Time.now (kbOffset cfg)).DateTime

    let digest (cfg: IConfiguration) (vault: IVault) (chat: IChatClient) (p: Digest.DigestParams) : Digest.DigestResult =
        let off = kbOffset cfg
        Digest.generate { Vault = vault; Chat = chat; Model = cfg.["Ollama:Model"]
                          Today = (KnowledgeBase.Time.now off).DateTime; UtcOffset = off } p

    let refilePeople (cfg: IConfiguration) (vault: IVault) (chat: IChatClient) : Refiler.RefileSummary =
        ignore cfg
        Refiler.run vault chat

/// Runs due scheduled tasks on a timer. Registered only when Scheduler:Enabled = "true".
/// `runTask` (built in Program.fs) dispatches by task name and swallows its own failures so one
/// failing task never aborts the tick or the others.
type SchedulerService
    (tasks: Scheduler.ScheduledTask list, stateStore: ISchedulerStateStore,
     runTask: Scheduler.ScheduledTask -> unit, checkSeconds: int,
     logger: ILogger<SchedulerService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                try
                    let state = stateStore.Load()
                    let next = Scheduler.tick DateTime.Now tasks state runTask
                    // Save once per tick (not per task); safe to re-run a task after a crash — digests are date-stamped and reindex is idempotent.
                    stateStore.Save next
                with ex ->
                    logger.LogWarning(ex, "scheduler tick failed")
                try do! Task.Delay(TimeSpan.FromSeconds(float checkSeconds), stoppingToken)
                with :? OperationCanceledException -> ()
        } :> Task
