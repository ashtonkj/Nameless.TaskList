namespace Nameless.TaskList.Core

open System.Text
open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Ports

module Indexer =

    type IndexSummary =
        { Tasks: int; Topics: int; Events: int; Commitments: int
          Notes: int; People: int; Channels: int; Skipped: int }

    // NOTE: must NOT be `private`. YamlDotNet (like System.Text.Json) only serializes
    // public types' members, so a private record serializes to `{}` — index files would
    // get an empty frontmatter instead of `type: Index` / title / last_updated.
    [<CLIMutable>]
    type IndexMeta = { Type: string; Title: string; LastUpdated: string }

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
        |> List.groupBy (fun (_, t) -> let s = (nz t.Status).ToLowerInvariant() in if s = "" then "unknown" else s)
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
        |> List.groupBy (fun (_, t) -> let s = (nz t.Status).ToLowerInvariant() in if s = "" then "unknown" else s)
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

    let private whenKey (s: string) =
        match System.DateTimeOffset.TryParse(s) with
        | true, dto -> dto.UtcDateTime
        | _ -> System.DateTime.MaxValue

    let private renderEvents (vault: IVault) : int * int =
        let items, skipped = loadAll<Event> vault "events"
        let sb = StringBuilder()
        for (path, e) in items |> List.sortBy (fun (path, e) -> (whenKey e.When, path)) do
            sb.AppendLine(sprintf "- %s — %s · %s" (wikilink path) (nz e.When) (joinArr e.Context)) |> ignore
        writeIndex vault "events" "Event Index" (sb.ToString().TrimEnd())
        List.length items, skipped

    let private commitmentStatusRank (s: string) =
        match (nz s).ToLowerInvariant() with "unresolved" -> 0 | "resolved" -> 1 | _ -> 2

    let private renderCommitments (vault: IVault) : int * int =
        let items, skipped = loadAll<Commitment> vault "commitments"
        let sb = StringBuilder()
        items
        |> List.sortBy (fun (path, c) -> (commitmentStatusRank c.Status, nz c.Due, path))
        |> List.groupBy (fun (_, c) -> let s = (nz c.Status).ToLowerInvariant() in if s = "" then "unknown" else s)
        |> List.iter (fun (status, rows) ->
            sb.AppendLine(sprintf "## %s" status) |> ignore
            for (path, c) in rows do
                let flag = if System.String.IsNullOrWhiteSpace c.TaskAssigned then " ⚑" else ""
                sb.AppendLine(sprintf "- %s — due %s · %s%s" (wikilink path) (nz c.Due) (nz c.Priority) flag) |> ignore
            sb.AppendLine() |> ignore)
        writeIndex vault "commitments" "Commitment Index" (sb.ToString().TrimEnd())
        List.length items, skipped

    let private firstContext (c: string array) =
        if isNull c || c.Length = 0 || System.String.IsNullOrWhiteSpace c.[0] then "uncategorised" else c.[0]

    let private renderNotes (vault: IVault) : int * int =
        let items, skipped = loadAll<Note> vault "notes"
        let sb = StringBuilder()
        items
        |> List.sortBy (fun (path, n) -> (firstContext n.Context, path))
        |> List.groupBy (fun (_, n) -> firstContext n.Context)
        |> List.iter (fun (ctx, rows) ->
            sb.AppendLine(sprintf "## %s" ctx) |> ignore
            for (path, n) in rows do
                sb.AppendLine(sprintf "- %s — %s" (wikilink path) (joinArr n.Tags)) |> ignore
            sb.AppendLine() |> ignore)
        writeIndex vault "notes" "Notes Index" (sb.ToString().TrimEnd())
        List.length items, skipped

    /// Context is the directory segment: people/{ctx}/{slug}.md
    let private peopleDirContext (path: string) =
        let parts = path.Split('/')
        if parts.Length >= 3 then parts.[1] else "uncategorised"

    let private renderPeople (vault: IVault) : int * int =
        let items, skipped = loadAll<Person> vault "people"
        let sb = StringBuilder()
        items
        |> List.sortBy (fun (path, _) -> (peopleDirContext path, path))
        |> List.groupBy (fun (path, _) -> peopleDirContext path)
        |> List.iter (fun (ctx, rows) ->
            sb.AppendLine(sprintf "## %s" ctx) |> ignore
            for (path, p) in rows do
                sb.AppendLine(sprintf "- %s — %s" (wikilink path) (nz p.Role)) |> ignore
            sb.AppendLine() |> ignore)
        writeIndex vault "people" "People Index" (sb.ToString().TrimEnd())
        List.length items, skipped

    let regenerate (vault: IVault) : IndexSummary =
        let tCount, tSkip = renderTasks vault
        let topCount, topSkip = renderTopics vault
        let evCount, evSkip = renderEvents vault
        let cmCount, cmSkip = renderCommitments vault
        let nCount, nSkip = renderNotes vault
        let pCount, pSkip = renderPeople vault
        let chCount, chSkip = renderChannels vault
        { Tasks = tCount; Topics = topCount; Events = evCount; Commitments = cmCount
          Notes = nCount; People = pCount; Channels = chCount
          Skipped = tSkip + topSkip + evSkip + cmSkip + nSkip + pSkip + chSkip }
