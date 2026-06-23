namespace Nameless.TaskList.Core

open System.Text
open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Ports

module Digest =

    type DigestKind = Daily | Weekly

    type DigestParams =
        { Kind: DigestKind; WindowDays: int; TopN: int }
        static member daily  = { Kind = Daily;  WindowDays = 7;  TopN = 5 }
        static member weekly = { Kind = Weekly; WindowDays = 14; TopN = 10 }

    type DigestDeps =
        { Vault: IVault; Chat: IChatClient; Model: string; Today: System.DateTime }

    type DigestResult =
        { Path: string; Text: string
          TaskCount: int; EventCount: int; CommitmentCount: int; StaleTopicCount: int }

    let private nz (s: string) = if isNull s then "" else s
    let private joinArr (a: string array) = if isNull a then "" else System.String.Join(", ", a)

    /// Load all parseable records of type 'T under root (excluding index.md); parse failures skipped.
    let private loadAll<'T> (vault: IVault) (root: string) : 'T list =
        vault.ListFilesRecursive root
        |> List.filter (fun p -> not (p.EndsWith("index.md")))
        |> List.choose (fun p ->
            try
                let mf = MarkdownFile.FromString (vault.Read p)
                match mf.FrontMatter with
                | Some fm -> Some(Frontmatter.deserialize<'T> fm)
                | None -> None
            with
            | :? System.IO.IOException -> reraise()
            | _ -> None)

    let private daysUntil (today: System.DateTime) (s: string) : int option =
        match System.DateTimeOffset.TryParse(s) with
        | true, dto -> Some((dto.Date - today.Date).Days)
        | _ -> None

    let private kindName (k: DigestKind) = match k with Daily -> "daily" | Weekly -> "weekly"

    let generate (deps: DigestDeps) (p: DigestParams) : DigestResult =
        let weights =
            let wp = "_meta/priority-weights.md"
            if deps.Vault.Exists wp then Weights.parse (deps.Vault.Read wp) else Weights.defaults

        let tasks = loadAll<Task> deps.Vault "tasks"
        let events = loadAll<Event> deps.Vault "events"
        let commitments = loadAll<Commitment> deps.Vault "commitments"
        let topics = loadAll<Topic> deps.Vault "topics"

        // Select
        let topTasks =
            tasks
            |> List.filter (fun t ->
                let s = (nz t.Status).ToLowerInvariant()
                s = "pending" || s = "in-progress")
            |> List.map (fun t -> t, Scoring.scoreTask weights deps.Today t)
            |> List.sortByDescending (fun (t, score) -> (score, nz t.Due, nz t.Title))
            |> List.truncate p.TopN

        let upcomingEvents =
            events
            |> List.choose (fun e ->
                match daysUntil deps.Today e.When with
                | Some d when d >= 0 && d <= p.WindowDays -> Some(d, e)
                | _ -> None)
            |> List.sortBy (fun (d, e) -> (d, nz e.Title))
            |> List.map snd

        let openCommitments =
            commitments
            |> List.filter (fun c ->
                (nz c.Status).ToLowerInvariant() = "unresolved"
                && System.String.IsNullOrWhiteSpace c.TaskAssigned)

        let staleTopics =
            topics
            |> List.filter (fun t ->
                (nz t.Status).ToLowerInvariant() = "active"
                && (match daysUntil deps.Today t.LastUpdated with
                    | Some d -> d < -14      // last_updated more than 14 days before today
                    | None -> false))

        // Render lists for the prompt / fallback
        let line s = "- " + s
        let renderTasks () =
            if List.isEmpty topTasks then "(none)"
            else topTasks |> List.map (fun (t, score) -> line (sprintf "%s · %s · due %s · score %d" (nz t.Title) (joinArr t.Context) (nz t.Due) score)) |> String.concat "\n"
        let renderEvents () =
            if List.isEmpty upcomingEvents then "(none)"
            else upcomingEvents |> List.map (fun e -> line (sprintf "%s · %s" (nz e.Title) (nz e.When))) |> String.concat "\n"
        let renderCommitments () =
            if List.isEmpty openCommitments then "(none)"
            else openCommitments |> List.map (fun c -> line (sprintf "%s · due %s" (nz c.Title) (nz c.Due))) |> String.concat "\n"
        let renderTopics () =
            if List.isEmpty staleTopics then "(none)"
            else staleTopics |> List.map (fun t -> line (sprintf "%s · last updated %s" (nz t.Title) (nz t.LastUpdated))) |> String.concat "\n"

        let payload =
            sprintf "Current date: %s\n\nPending tasks (sorted by priority score):\n%s\n\nUpcoming events (next %d days):\n%s\n\nUnresolved commitments:\n%s\n\nStale topics (no activity in 14+ days):\n%s"
                (deps.Today.ToString("yyyy-MM-dd")) (renderTasks ()) p.WindowDays (renderEvents ()) (renderCommitments ()) (renderTopics ())

        let systemPrompt = match p.Kind with Daily -> Prompts.dailyBriefingSystem | Weekly -> Prompts.weeklyDigestSystem

        // Deterministic fallback body used if the LLM call fails.
        let fallbackBody () =
            let sb = StringBuilder()
            sb.AppendLine(sprintf "# %s digest — %s" (kindName p.Kind) (deps.Today.ToString("yyyy-MM-dd"))) |> ignore
            sb.AppendLine().AppendLine("## Top tasks").AppendLine(renderTasks ()) |> ignore
            sb.AppendLine().AppendLine(sprintf "## Upcoming events (next %d days)" p.WindowDays).AppendLine(renderEvents ()) |> ignore
            sb.AppendLine().AppendLine("## Open commitments").AppendLine(renderCommitments ()) |> ignore
            sb.AppendLine().AppendLine("## Stale topics").AppendLine(renderTopics ()) |> ignore
            sb.ToString().TrimEnd()

        let body =
            try Agent.runConversation deps.Chat [] systemPrompt payload
            with ex ->
                eprintfn "Digest LLM call failed (%s); using deterministic fallback." ex.Message
                fallbackBody ()

        let kind = kindName p.Kind
        let path = Naming.digestPath deps.Today kind
        let record : Digest =
            { Type = "Digest"; Title = sprintf "%s digest %s" kind (deps.Today.ToString("yyyy-MM-dd"))
              Kind = kind; Generated = deps.Today.ToString("yyyy-MM-ddTHH:mm:sszzz") }
        deps.Vault.Write(path, MarkdownFile.ToString (Frontmatter.serialize record) body)

        { Path = path; Text = body
          TaskCount = List.length topTasks; EventCount = List.length upcomingEvents
          CommitmentCount = List.length openCommitments; StaleTopicCount = List.length staleTopics }
