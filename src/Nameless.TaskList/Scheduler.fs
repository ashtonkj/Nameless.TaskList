namespace Nameless.TaskList

open System
open Microsoft.Extensions.Configuration
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports

/// The digest/reindex invocations shared by the HTTP endpoints and the in-app scheduler,
/// so a manual run and a scheduled run execute identical code reading the same config.
module MaintenanceTasks =

    let private topicCfg (cfg: IConfiguration) : Indexer.TopicSweepConfig =
        { ResolvedArchiveAfterDays = (match Int32.TryParse(cfg.["Topics:ResolvedArchiveAfterDays"]) with | true, n -> n | _ -> 14)
          DormantArchiveAfterDays = (match Int32.TryParse(cfg.["Topics:DormantArchiveAfterDays"]) with | true, n -> n | _ -> 90) }

    let reindex (cfg: IConfiguration) (vault: IVault) : Indexer.IndexSummary =
        Indexer.regenerate vault (topicCfg cfg) DateTime.Now

    let digest (cfg: IConfiguration) (vault: IVault) (chat: IChatClient) (p: Digest.DigestParams) : Digest.DigestResult =
        Digest.generate { Vault = vault; Chat = chat; Model = cfg.["Ollama:Model"]; Today = DateTime.Now } p
