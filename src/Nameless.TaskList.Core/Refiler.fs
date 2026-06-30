namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Ports

/// Re-files people into the context their role implies (LLM-classified). A maintenance pass:
/// scans people/**, moves any whose folder disagrees with the classified context, and fixes the
/// Context frontmatter so the folder and Context[0] agree. Active->active via IVault.Relocate.
module Refiler =

    /// The routing decision for one person. Pure; the move/IO happens in `run`.
    type RefileAction =
        | NoChange
        | Refile of target: string
        | SkipCollision
        | SkipUnknown

    /// Decide what to do given the current context folder, the classified target (None = failed/
    /// off-list), and whether the target path is already occupied.
    let decideRefile (currentContext: string) (target: string option) (targetExists: bool) : RefileAction =
        match target with
        | None -> SkipUnknown
        | Some t when t = currentContext -> NoChange
        | Some _ when targetExists -> SkipCollision
        | Some t -> Refile t

    /// Counts for one pass. Scanned = people parsed; Refiled = moved; Skipped = collision/off-list/
    /// failed. Already-correct people are neither refiled nor skipped (Scanned - Refiled - Skipped).
    type RefileSummary = { Scanned: int; Refiled: int; Skipped: int }

    /// Re-file every person under people/**. Best-effort per person (any failure -> skipped).
    let run (vault: IVault) (chat: IChatClient) : RefileSummary =
        let mutable scanned = 0
        let mutable refiled = 0
        let mutable skipped = 0
        for path in vault.ListFilesRecursive "people" do
            try
                let segments = path.Split('/')
                // Expect people/<ctx>/<slug>.md (3 segments); anything else is not a filed person.
                if segments.Length = 3 && segments.[0] = "people" then
                    let mf = MarkdownFile.FromString (vault.Read path)
                    match mf.FrontMatter with
                    | Some fm ->
                        let p = Frontmatter.deserialize<Person> fm
                        scanned <- scanned + 1
                        let currentContext = segments.[1]
                        let slug = System.IO.Path.GetFileNameWithoutExtension path
                        let target = Steps.classifyPersonContext chat p.Title p.Role p.Aliases
                        let newPath = match target with | Some t -> Naming.personPath t slug | None -> ""
                        let targetExists = target.IsSome && vault.Exists newPath
                        match decideRefile currentContext target targetExists with
                        | Refile t ->
                            vault.Relocate(path, newPath)
                            // Relocate gives no success signal: confirm the move before patching.
                            if vault.Exists newPath && not (vault.Exists path) then
                                let moved = MarkdownFile.FromString (vault.Read newPath)
                                match moved.FrontMatter with
                                | Some mfm ->
                                    let mp = Frontmatter.deserialize<Person> mfm
                                    let others =
                                        if isNull mp.Context then [||]
                                        else mp.Context |> Array.filter (fun c -> c <> t)
                                    let updated = { mp with Context = Array.append [| t |] others }
                                    vault.Write(newPath, MarkdownFile.ToString (Frontmatter.serialize updated) moved.Content)
                                | None -> ()
                                refiled <- refiled + 1
                            else
                                skipped <- skipped + 1
                        | SkipCollision -> skipped <- skipped + 1
                        | SkipUnknown -> skipped <- skipped + 1
                        | NoChange -> ()
                    | None -> ()
            with _ ->
                skipped <- skipped + 1
        { Scanned = scanned; Refiled = refiled; Skipped = skipped }
