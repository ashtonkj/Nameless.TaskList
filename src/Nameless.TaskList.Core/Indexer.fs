namespace Nameless.TaskList.Core

open System.Text
open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Ports

module Indexer =

    type IndexSummary =
        { Tasks: int; Topics: int; Events: int; Commitments: int
          Notes: int; People: int; Channels: int; Skipped: int }

    [<CLIMutable>]
    type private IndexMeta = { Type: string; Title: string; LastUpdated: string }

    let private nz (s: string) = if isNull s then "" else s
    let private joinArr (a: string array) = if isNull a then "" else System.String.Join(", ", a)

    let private wikilink (path: string) =
        let p = if path.EndsWith(".md") then path.[.. path.Length - 4] else path
        sprintf "[[%s]]" p

    /// Load all parseable records of type 'T under root (excluding any index.md); returns (path, record) list and a skip count.
    let private loadAll<'T> (vault: IVault) (root: string) : (string * 'T) list * int =
        let files =
            vault.ListFilesRecursive root
            |> List.filter (fun p -> not (p.EndsWith("index.md")))
        let mutable skipped = 0
        let parsed =
            files
            |> List.choose (fun p ->
                try
                    let mf = MarkdownFile.FromString (vault.Read p)
                    match mf.FrontMatter with
                    | Some fm -> Some(p, Frontmatter.deserialize<'T> fm)
                    | None -> skipped <- skipped + 1; None
                with
                | :? System.IO.IOException -> reraise()
                | _ -> skipped <- skipped + 1; None)
        parsed, skipped

    let private writeIndex (vault: IVault) (root: string) (title: string) (body: string) =
        let meta : IndexMeta =
            { Type = "Index"; Title = title; LastUpdated = System.DateTime.UtcNow.ToString("yyyy-MM-dd") }
        vault.Write(root.TrimEnd('/') + "/index.md", MarkdownFile.ToString (Frontmatter.serialize meta) body)

    let private priorityRank (p: string) =
        match (nz p).ToLowerInvariant() with
        | "critical" -> 0 | "high" -> 1 | "medium" -> 2 | "low" -> 3 | _ -> 4

    let private taskStatusRank (s: string) =
        match (nz s).ToLowerInvariant() with
        | "pending" -> 0 | "in-progress" -> 1 | "blocked" -> 2 | "done" -> 3 | "cancelled" -> 4 | _ -> 5

    let private renderTasks (vault: IVault) : int * int =
        let items, skipped = loadAll<Task> vault "tasks"
        let sb = StringBuilder()
        items
        |> List.sortBy (fun (path, t) -> (taskStatusRank t.Status, priorityRank t.Priority, nz t.Due, path))
        |> List.groupBy (fun (_, t) -> (if nz t.Status = "" then "unknown" else t.Status))
        |> List.iter (fun (status, rows) ->
            sb.AppendLine(sprintf "## %s" status) |> ignore
            for (path, t) in rows do
                sb.AppendLine(sprintf "- %s — due %s · %s" (wikilink path) (nz t.Due) (joinArr t.Context)) |> ignore
            sb.AppendLine() |> ignore)
        writeIndex vault "tasks" "Task Index" (sb.ToString().TrimEnd())
        List.length items, skipped

    let private topicStatusRank (s: string) =
        match (nz s).ToLowerInvariant() with "active" -> 0 | "resolved" -> 1 | _ -> 2

    let private renderTopics (vault: IVault) : int * int =
        let items, skipped = loadAll<Topic> vault "topics"
        let sb = StringBuilder()
        items
        |> List.sortBy (fun (_, t) -> topicStatusRank t.Status)
        |> List.groupBy (fun (_, t) -> (if nz t.Status = "" then "unknown" else t.Status))
        |> List.iter (fun (status, rows) ->
            sb.AppendLine(sprintf "## %s" status) |> ignore
            for (path, t) in rows |> List.sortByDescending (fun (_, t) ->
                                        let lu = nz t.LastUpdated
                                        (not (System.String.IsNullOrWhiteSpace lu), lu)) do
                sb.AppendLine(sprintf "- %s — last updated %s" (wikilink path) (nz t.LastUpdated)) |> ignore
            sb.AppendLine() |> ignore)
        writeIndex vault "topics" "Topic Index" (sb.ToString().TrimEnd())
        List.length items, skipped

    let private renderChannels (vault: IVault) : int * int =
        let items, skipped = loadAll<Channel> vault "channels"
        let sb = StringBuilder()
        for (path, c) in items |> List.sortByDescending (fun (_, c) -> c.LastProcessed) do
            let activeCount = if isNull c.ActiveTopics then 0 else c.ActiveTopics.Length
            sb.AppendLine(sprintf "- %s — %s · last processed %s · %d active"
                              (wikilink path) (nz c.SignalWeight) (c.LastProcessed.ToString("yyyy-MM-dd")) activeCount) |> ignore
        writeIndex vault "channels" "Channel Index" (sb.ToString().TrimEnd())
        List.length items, skipped

    let regenerate (vault: IVault) : IndexSummary =
        let tCount, tSkip = renderTasks vault
        let topCount, topSkip = renderTopics vault
        let chCount, chSkip = renderChannels vault
        // events/commitments/notes/people renderers are added in the next task.
        { Tasks = tCount; Topics = topCount; Events = 0; Commitments = 0
          Notes = 0; People = 0; Channels = chCount
          Skipped = tSkip + topSkip + chSkip }
