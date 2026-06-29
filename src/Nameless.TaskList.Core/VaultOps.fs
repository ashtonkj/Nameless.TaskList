namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Ports

/// Higher-level vault operations composed over the IVault primitives.
module Vault =

    /// Retire an active file to the .trash/ area (bytes preserved, original path vacated).
    /// Best-effort: a relocate failure (or a missing source) is swallowed so it never breaks
    /// the caller. `ts` is the configured-offset wall clock (callers pass (Time.now offset).DateTime).
    let retire (vault: IVault) (ts: System.DateTime) (relPath: string) : unit =
        try vault.Relocate(relPath, Naming.trashPath ts relPath) with _ -> ()
