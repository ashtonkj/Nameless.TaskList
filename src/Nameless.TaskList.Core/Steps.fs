namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Ports

/// Per-step LLM calls extracted from Pipeline.processMessage so the pipeline and the
/// eval harness call exactly the same prompt+parse+tool-loop code (single source of truth).
module Steps =

    /// Terms of endearment / pet names a partner uses ("hey pookie") are not identifiable
    /// people — left in, they spawn a junk person file. Drop them from a mentions list.
    let private endearments =
        set [ "pookie"; "babe"; "baby"; "bae"; "boo"; "hun"; "hon"; "honey"; "sweetie"
              "sweetheart"; "sweetpea"; "love"; "lovey"; "lovie"; "darling"; "dear"; "dearest"
              "hubby"; "wifey"; "snookums"; "cutie"; "pumpkin"; "sugar"; "my love"; "my dear"
              "my darling"; "my dear wife"; "my dear husband" ]

    let stripEndearments (people: string array) : string array =
        if isNull people then [||]
        else people |> Array.filter (fun p ->
            not (endearments.Contains((if isNull p then "" else p).Trim().ToLowerInvariant())))

    /// Strip surrounding code fences / leading prose from a model reply.
    let stripFences (text: string) =
        let trimmed = (if isNull text then "" else text).Trim()
        let idx = trimmed.IndexOf("```")
        if idx >= 0 then
            let afterFirst = trimmed.IndexOf('\n', idx)
            let lastFence = trimmed.LastIndexOf("```")
            if afterFirst > 0 && lastFence > afterFirst then trimmed.[afterFirst..lastFence - 1].Trim()
            else trimmed
        else trimmed

    /// Map an urgency string to a priority value.
    let urgencyToPriority (u: string) =
        match (if isNull u then "" else u).ToLowerInvariant() with
        | "critical" -> "critical"
        | "high" -> "high"
        | "low" -> "low"
        | _ -> "medium"

    /// Canonicalize a people array to distinct non-empty slugs (matching person filenames).
    let slugifyPeople (a: string array) =
        if isNull a then [||]
        else a |> Array.map Naming.slug |> Array.filter (fun s -> s <> "") |> Array.distinct

    /// The outcome of a generation step: the parsed record plus its markdown body.
    type EntityOutcome<'T> = { Record: 'T; Body: string }

    /// Inputs a generative creator needs: the model-facing fields (intent, raw message,
    /// pre-formatted reference timestamp, contexts, urgency) plus the pipeline-owned linkage
    /// the creator stamps onto the record (topic/message paths, people slugs, linked task paths).
    /// The eval passes neutral stubs for the linkage fields.
    type GenInput =
        { Intent: string
          Raw: string
          ReferenceDate: string
          Contexts: string array
          Urgency: string
          TopicPath: string
          MessagePath: string
          PeopleSlugs: string array
          TaskPaths: string list }

    /// A classify failure carries both the parse error and the raw model reply, so the
    /// pipeline can keep its exact [classify-error] log line (id/chat/reason/raw).
    type ClassifyError = { Message: string; Raw: string }

    /// Classify + extract from one message. Tool-enabled (get_contexts/get_people/
    /// get_relationships over the vault). Applies the endearment strip the pipeline relied on.
    let classify (chat: IChatClient) (vault: IVault) (history: string) (content: string)
        : Result<Prompts.Classification, ClassifyError> =
        let classifyTools = [ Tools.getContexts vault; Tools.getPeople vault; Tools.getRelationships vault ]
        let reply =
            Agent.runConversation chat classifyTools Prompts.classifySystem (Prompts.classifyUser history content)
        match Prompts.parseClassification reply with
        | Error e -> Error { Message = e; Raw = (if isNull reply then "<null>" else reply) }
        | Ok c -> Ok { c with PeopleMentioned = stripEndearments c.PeopleMentioned }

    /// Extract the "## Current understanding" section of a topic body (fallback: whole body).
    let private understandingOf (body: string) =
        let b = if isNull body then "" else body
        let marker = "## Current understanding"
        let i = b.IndexOf(marker)
        if i < 0 then b.Trim()
        else
            let after = b.Substring(i + marker.Length)
            let next = after.IndexOf("\n## ")
            (if next < 0 then after else after.Substring(0, next)).Trim()

    /// A concise topic title derived from an intent (used only by the clearly-new fast path).
    let private titleFromIntent (intent: string) =
        let s = (if isNull intent then "" else intent).Trim()
        if s.Length <= 60 then s else s.Substring(0, 60).Trim()

    /// The decision the topic-match step produces. File creation stays in the pipeline; this
    /// is only the prompt-driven routing choice (match an existing topic, or make a new one).
    type TopicDecision =
        | MatchExisting of slug: string
        | CreateTopic of title: string

    /// Decide whether `intent` matches an existing active topic (embedding shortlist + LLM
    /// confirm; tool-enabled fallback when embedding is unavailable). Reads active topics from
    /// the vault; never writes. Behaviour mirrors the former Pipeline topicOutcome exactly.
    let matchTopic
        (chat: IChatClient) (embedder: IEmbedder) (vault: IVault)
        (topK: int) (similarityFloor: float) (intent: string)
        : Result<TopicDecision, string> =

        let topicFiles = vault.ListFiles "topics/active"
        let activeTopics =
            topicFiles
            |> List.choose (fun path ->
                try
                    let mf = MarkdownFile.FromString (vault.Read path)
                    match mf.FrontMatter with
                    | Some fm ->
                        let t = Frontmatter.deserialize<Topic> fm
                        if (if isNull t.Status then "active" else t.Status).Trim().ToLowerInvariant() = "archived" then None
                        else Some (System.IO.Path.GetFileNameWithoutExtension(path), t.Title, understandingOf mf.Content)
                    | None -> None
                with _ -> None)

        // Some candidates (possibly empty) when embedding works; None to fall back to tools.
        let shortlist =
            if List.isEmpty topicFiles then None
            elif List.isEmpty activeTopics then Some []
            else
                try
                    let intentVec = embedder.Embed intent
                    activeTopics
                    |> List.map (fun (slug, title, und) ->
                        let score = Similarity.cosine intentVec (embedder.Embed (title + "\n" + und))
                        (slug, title, und, score))
                    |> List.filter (fun (_, _, _, s) -> s >= similarityFloor)
                    |> List.sortByDescending (fun (_, _, _, s) -> s)
                    |> List.truncate topK
                    |> Some
                with _ -> None

        match shortlist with
        | Some [] ->
            Ok (CreateTopic (titleFromIntent intent))
        | Some candidates ->
            let candidateText =
                candidates
                |> List.map (fun (slug, title, und, _) -> sprintf "slug: %s\ntitle: %s\nunderstanding: %s" slug title und)
                |> String.concat "\n\n"
            let payload = sprintf "New message intent: %s\n\nCandidate topics:\n%s" intent candidateText
            match Prompts.parseTopicMatch (Agent.runConversation chat [] Prompts.topicMatchSystem payload) with
            | Error e -> Error e
            | Ok m ->
                let normalized = (if isNull m.TopicSlug then "" else m.TopicSlug).Trim().ToLowerInvariant()
                let matched = candidates |> List.tryFind (fun (s, _, _, _) -> s.ToLowerInvariant() = normalized)
                match m.Match, matched with
                | true, Some (slug, _, _, _) -> Ok (MatchExisting slug)
                | _ ->
                    let title = if System.String.IsNullOrWhiteSpace m.NewTopicTitle then titleFromIntent intent else m.NewTopicTitle
                    Ok (CreateTopic title)
        | None ->
            let topicTools = [ Tools.getTopics vault; Tools.getTopic vault ]
            let reply = Agent.runConversation chat topicTools Prompts.topicMatchSystem (sprintf "New message intent: %s" intent)
            match Prompts.parseTopicMatch reply with
            | Error e -> Error e
            | Ok m ->
                if m.Match && not (System.String.IsNullOrWhiteSpace m.TopicSlug) then Ok (MatchExisting m.TopicSlug)
                else Ok (CreateTopic m.NewTopicTitle)

    /// Generate a Task record+body from one intent. Mirrors the former Pipeline taskSpec
    /// (prompt + user message + parse/fallback). Linkage (Topic/SourceMessage/People) is set
    /// from `input`; the model only supplies title/description/status/priority/due/context/body.
    let createTask (chat: IChatClient) (input: GenInput) : EntityOutcome<Task> =
        let user =
            sprintf "Message intent: %s\nRaw message: %s\nMessage reference date (resolve relative dates like \"tomorrow\" against this): %s\nContext(s): %s\nUrgency: %s\nSource message file: %s"
                input.Intent input.Raw input.ReferenceDate (String.concat ", " input.Contexts) input.Urgency input.MessagePath
        let stripped = stripFences (Agent.runConversation chat [] Prompts.taskCreateSystem user)
        try
            let parsed = MarkdownFile.FromString stripped
            match parsed.FrontMatter with
            | Some fm ->
                let t = Frontmatter.deserialize<Task> fm
                if not (System.String.IsNullOrWhiteSpace t.Title) then
                    { Record = { t with Type = "Task"; Description = (if System.String.IsNullOrWhiteSpace t.Description then input.Intent else t.Description); Topic = input.TopicPath; SourceMessage = input.MessagePath; People = slugifyPeople t.People }
                      Body = parsed.Content }
                else raise (System.Exception("empty title"))
            | None -> raise (System.Exception("no frontmatter"))
        with _ ->
            let fb : Task =
                { Type = "Task"; Title = input.Intent; Description = input.Intent; Status = "pending"
                  Priority = urgencyToPriority input.Urgency; Due = ""
                  Context = input.Contexts; People = input.PeopleSlugs
                  Topic = input.TopicPath; SourceMessage = input.MessagePath }
            { Record = fb; Body = input.Intent }

    /// Generate an Event record+body from one intent. Mirrors the former Pipeline eventSpec,
    /// including the ensureDated path: an unparseable `when` falls back to the reference date
    /// and appends the "date inferred" body flag.
    let createEvent (chat: IChatClient) (input: GenInput) : EntityOutcome<Event> =
        let user =
            sprintf "Event intent: %s\nRaw message: %s\nMessage reference date: %s\nContext(s): %s\nSource message file: %s"
                input.Intent input.Raw input.ReferenceDate (String.concat ", " input.Contexts) input.MessagePath
        let stripped = stripFences (Agent.runConversation chat [] Prompts.eventCreateSystem user)
        let flag = "\n\n_Date inferred from message; please confirm._"
        let ensureDated (e: Event) (body: string) =
            match System.DateTimeOffset.TryParse(e.When) with
            | true, _ -> { Record = e; Body = body }
            | _ -> { Record = { e with When = input.ReferenceDate }; Body = body + flag }
        try
            let parsed = MarkdownFile.FromString stripped
            match parsed.FrontMatter with
            | Some fm ->
                let e = Frontmatter.deserialize<Event> fm
                if not (System.String.IsNullOrWhiteSpace e.Title) then
                    ensureDated { e with Type = "Event"; Description = (if System.String.IsNullOrWhiteSpace e.Description then input.Intent else e.Description); Topic = input.TopicPath; TasksLinked = Array.ofList input.TaskPaths; People = slugifyPeople e.People } parsed.Content
                else raise (System.Exception("empty title"))
            | None -> raise (System.Exception("no frontmatter"))
        with _ ->
            let fb : Event =
                { Type = "Event"; Title = input.Intent; Description = input.Intent; When = input.ReferenceDate; AllDay = true
                  Context = input.Contexts; Location = ""; People = input.PeopleSlugs
                  Topic = input.TopicPath; TasksLinked = Array.ofList input.TaskPaths; ReminderDaysBefore = 3 }
            { Record = fb; Body = input.Intent + flag }

    /// Generate a Note record+body from one intent. Mirrors the former Pipeline createNewNote +
    /// interpretNote. Source/PeopleLinked are pipeline-owned (set from input); the model supplies
    /// title/description/context/tags/body.
    let createNote (chat: IChatClient) (input: GenInput) : EntityOutcome<Note> =
        let user =
            sprintf "Note intent: %s\nRaw message: %s\nContext(s): %s\nSource message file: %s"
                input.Intent input.Raw (String.concat ", " input.Contexts) input.MessagePath
        let stripped = stripFences (Agent.runConversation chat [] Prompts.noteCreateSystem user)
        try
            let parsed = MarkdownFile.FromString stripped
            match parsed.FrontMatter with
            | Some fm ->
                let n = Frontmatter.deserialize<Note> fm
                if not (System.String.IsNullOrWhiteSpace n.Title) then
                    { Record = { n with Type = "Note"; Description = (if System.String.IsNullOrWhiteSpace n.Description then input.Intent else n.Description); Source = input.MessagePath; PeopleLinked = input.PeopleSlugs }
                      Body = parsed.Content }
                else raise (System.Exception("empty title"))
            | None -> raise (System.Exception("no frontmatter"))
        with _ ->
            { Record =
                { Type = "Note"; Title = input.Intent; Description = input.Intent; Context = input.Contexts
                  PeopleLinked = input.PeopleSlugs; Tags = [||]
                  Source = input.MessagePath; LastVerified = "" }
              Body = input.Intent }

    /// Shared shortlist-and-confirm core for the match sites (tasks, notes, people).
    /// Embeds queryText, scores each candidate's embedText by cosine, keeps those >= floor,
    /// takes the top topK, then asks the model (systemPrompt over buildPayload displayLines) to
    /// confirm a match and returns the matched slug. Best-effort: empty candidates / embedder
    /// failure / parse error / no confirmed match all yield None.
    let shortlistAndConfirm
        (chat: IChatClient) (embedder: IEmbedder) (queryText: string)
        (candidates: (string * string * string) list)
        (floor: float) (topK: int) (systemPrompt: string)
        (buildPayload: string list -> string) : string option =
        if List.isEmpty candidates then None
        else
            try
                let q = embedder.Embed queryText
                let shortlisted =
                    candidates
                    |> List.map (fun (slug, embedText, line) -> slug, line, Similarity.cosine q (embedder.Embed embedText))
                    |> List.filter (fun (_, _, s) -> s >= floor)
                    |> List.sortByDescending (fun (_, _, s) -> s)
                    |> List.truncate topK
                    |> List.map (fun (slug, line, _) -> slug, line)
                match shortlisted with
                | [] -> None
                | sl ->
                    let payload = buildPayload (sl |> List.map snd)
                    match Prompts.parseTopicMatch (Agent.runConversation chat [] systemPrompt payload) with
                    | Ok m when m.Match ->
                        let normalized = (if isNull m.TopicSlug then "" else m.TopicSlug).Trim().ToLowerInvariant()
                        sl |> List.tryPick (fun (slug, _) -> if slug.ToLowerInvariant() = normalized then Some slug else None)
                    | _ -> None
            with _ -> None

    /// Decide whether `intent` matches an existing pending task (None = no confirmed match).
    let matchTask (chat: IChatClient) (embedder: IEmbedder) (vault: IVault) (intent: string) (floor: float) (topK: int) : string option =
        let existingTasks =
            vault.ListFiles "tasks/pending"
            |> List.choose (fun path ->
                try
                    let mf = MarkdownFile.FromString (vault.Read path)
                    match mf.FrontMatter with
                    | Some fm ->
                        let t = Frontmatter.deserialize<Task> fm
                        let slug = System.IO.Path.GetFileNameWithoutExtension(path)
                        let summary = mf.Content.Trim()
                        Some (slug, t.Title + "\n" + summary, sprintf "slug: %s\ntitle: %s\nsummary: %s" slug t.Title summary)
                    | None -> None
                with _ -> None)
        let buildPayload lines = sprintf "New task intent: %s\n\nCandidate tasks:\n%s" intent (String.concat "\n\n" lines)
        shortlistAndConfirm chat embedder intent existingTasks floor topK Prompts.taskMatchSystem buildPayload

    /// Decide whether `intent` matches an existing note (None = no confirmed match).
    let matchNote (chat: IChatClient) (embedder: IEmbedder) (vault: IVault) (intent: string) (floor: float) (topK: int) : string option =
        let existingNotes =
            vault.ListFiles "notes"
            |> List.choose (fun path ->
                try
                    let mf = MarkdownFile.FromString (vault.Read path)
                    match mf.FrontMatter with
                    | Some fm ->
                        let n = Frontmatter.deserialize<Note> fm
                        let slug = System.IO.Path.GetFileNameWithoutExtension(path)
                        let summary = mf.Content.Trim()
                        Some (slug, n.Title + "\n" + summary, sprintf "slug: %s\ntitle: %s\nsummary: %s" slug n.Title summary)
                    | None -> None
                with _ -> None)
        let buildPayload lines = sprintf "New note intent: %s\n\nCandidate notes:\n%s" intent (String.concat "\n\n" lines)
        shortlistAndConfirm chat embedder intent existingNotes floor topK Prompts.noteMatchSystem buildPayload

    /// Fuzzy person match (the second chance after exact alias-resolution fails in the pipeline).
    /// Returns the title-slug of the matched person, or None. Candidate slug is Naming.slug of the
    /// person's Title (matching the pipeline's resolvePerson key), NOT the filename.
    let matchPerson (chat: IChatClient) (embedder: IEmbedder) (vault: IVault) (name: string) (contexts: string array) (floor: float) (topK: int) : string option =
        let existingPeople =
            vault.ListFilesRecursive "people"
            |> List.choose (fun path ->
                try
                    let mf = MarkdownFile.FromString (vault.Read path)
                    match mf.FrontMatter with
                    | Some fm ->
                        let p = Frontmatter.deserialize<Person> fm
                        let aliases = if isNull p.Aliases then "" else String.concat " " (Array.toList p.Aliases)
                        let role = sprintf "%s %s" (if isNull p.Role then "" else p.Role) aliases
                        let slug = Naming.slug p.Title
                        Some (slug, p.Title + "\n" + role, sprintf "slug: %s\ntitle: %s\nrole: %s" slug p.Title role)
                    | None -> None
                with _ -> None)
        let buildPayload lines =
            sprintf "New person mention: %s\nContext: %s\n\nCandidate people:\n%s"
                name (String.concat ", " contexts) (String.concat "\n\n" lines)
        let queryText = sprintf "%s\n%s" name (String.concat ", " contexts)
        shortlistAndConfirm chat embedder queryText existingPeople floor topK Prompts.personMatchSystem buildPayload

    /// Run the task-update prompt and parse the model's merged task. Ok = parsed record+body;
    /// Error carries the stripped raw reply (the pipeline falls back to the OLD record + that raw
    /// body on Error, preserving today's behaviour). Never throws on bad model output.
    let updateTask (chat: IChatClient) (existingFile: string) (intent: string) (raw: string) : Result<EntityOutcome<Task>, string> =
        let updatedRaw =
            Agent.runConversation chat [] Prompts.taskUpdateSystem (Prompts.taskUpdateUser existingFile intent raw)
            |> stripFences
        try
            let parsed = MarkdownFile.FromString updatedRaw
            match parsed.FrontMatter with
            | Some nfm -> Ok { Record = Frontmatter.deserialize<Task> nfm; Body = parsed.Content }
            | None -> Error updatedRaw
        with _ -> Error updatedRaw

    /// Run the note-update prompt; returns the model's updated body (noteUpdateSystem emits body-only).
    let updateNote (chat: IChatClient) (existingBody: string) (intent: string) (raw: string) : string =
        Agent.runConversation chat [] Prompts.noteUpdateSystem (Prompts.noteUpdateUser existingBody intent raw)
        |> stripFences

    /// Generate a Commitment record+body from one intent. Mirrors the former Pipeline commitmentSpec.
    let createCommitment (chat: IChatClient) (input: GenInput) : EntityOutcome<Commitment> =
        let user =
            sprintf "Commitment intent: %s\nRaw message: %s\nReference date (resolve relative dates against this): %s\nContext(s): %s\nUrgency: %s\nSource message file: %s"
                input.Intent input.Raw input.ReferenceDate (String.concat ", " input.Contexts) input.Urgency input.MessagePath
        let stripped = stripFences (Agent.runConversation chat [] Prompts.commitmentCreateSystem user)
        try
            let parsed = MarkdownFile.FromString stripped
            match parsed.FrontMatter with
            | Some fm ->
                let c = Frontmatter.deserialize<Commitment> fm
                if not (System.String.IsNullOrWhiteSpace c.Title) then
                    { Record = { c with Type = "Commitment"; Description = (if System.String.IsNullOrWhiteSpace c.Description then input.Intent else c.Description); Topic = input.TopicPath; SourceMessage = input.MessagePath }
                      Body = parsed.Content }
                else raise (System.Exception("empty title"))
            | None -> raise (System.Exception("no frontmatter"))
        with _ ->
            let fb : Commitment =
                { Type = "Commitment"; Title = input.Intent; Description = input.Intent; Status = "unresolved"
                  Priority = urgencyToPriority input.Urgency; Due = ""
                  Context = input.Contexts; Topic = input.TopicPath
                  TaskAssigned = ""; EscalateAfterDays = 7; SourceMessage = input.MessagePath }
            { Record = fb; Body = input.Intent }
