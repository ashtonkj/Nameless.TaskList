namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Ports

module Pipeline =

    type PipelineDeps =
        { Messages: IMessageSource
          Vault: IVault
          Chat: IChatClient
          Model: string
          Embedder: IEmbedder
          TopK: int
          SimilarityFloor: float
          NoteTopK: int
          NoteSimilarityFloor: float
          TaskTopK: int
          TaskSimilarityFloor: float
          PeopleTopK: int
          PeopleSimilarityFloor: float
          Vision: IVision
          Transcriber: ITranscriber }

    type PipelineResult =
        | NotFound
        | Skipped
        | ProcessedNoise
        | Processed of topic: string * tasks: string list
        | LlmError of string

    let private isoTimestamp (ts: System.DateTime) = ts.ToString("yyyy-MM-ddTHH:mm:sszzz")

    /// The later of an existing ISO-8601 timestamp and a new message time. Topic
    /// last_updated must never regress when an out-of-order (older) message is matched
    /// into a topic, which would otherwise push last_updated below first_seen.
    let private laterIso (existing: string) (ts: System.DateTime) : string =
        let nu = isoTimestamp ts
        match System.DateTimeOffset.TryParse existing, System.DateTimeOffset.TryParse nu with
        | (true, prev), (true, cur) -> if prev > cur then existing else nu
        | _ -> nu

    /// Strip surrounding code fences / leading prose from a model reply.
    let private stripFences (text: string) =
        let trimmed = (if isNull text then "" else text).Trim()
        let idx = trimmed.IndexOf("```")
        if idx >= 0 then
            let afterFirst = trimmed.IndexOf('\n', idx)
            let lastFence = trimmed.LastIndexOf("```")
            if afterFirst > 0 && lastFence > afterFirst then trimmed.[afterFirst..lastFence - 1].Trim()
            else trimmed
        else trimmed

    /// Map an urgency string to a priority value.
    let private urgencyToPriority (u: string) =
        match (if isNull u then "" else u).ToLowerInvariant() with
        | "critical" -> "critical"
        | "high" -> "high"
        | "low" -> "low"
        | _ -> "medium"

    let private knownContexts = [ "family"; "medical"; "school"; "finance"; "professional" ]

    // Build a (slug -> person file path) index over every person's filename, title + aliases.
    // The filename slug is included as a key because curated person files are often named by a
    // short canonical slug (e.g. aleks-ashton.md) that differs from the full-name title
    // ("Aleksandra Anna Ashton"); without it a model proposing the canonical short name fails
    // to resolve and a duplicate stub is written in another context folder.
    let private peopleIndex (vault: IVault) : (string * string list) list =
        vault.ListFilesRecursive "people"
        |> List.choose (fun path ->
            try
                let mf = MarkdownFile.FromString (vault.Read path)
                match mf.FrontMatter with
                | Some fm ->
                    let p = Frontmatter.deserialize<Person> fm
                    let aliasSlugs = if isNull p.Aliases then [] else p.Aliases |> Array.toList |> List.map Naming.slug
                    let fileSlug = Naming.slug (System.IO.Path.GetFileNameWithoutExtension path)
                    let keys =
                        (fileSlug :: Naming.slug p.Title :: aliasSlugs)
                        |> List.filter (fun s -> s <> "")
                        |> List.distinct
                    Some (path, keys)
                | None -> None
            with _ -> None)

    /// Resolve a mention slug to an existing person file via title or alias (exact, normalized).
    let private resolvePerson (index: (string * string list) list) (mentionSlug: string) : string option =
        index |> List.tryPick (fun (path, keys) -> if List.contains mentionSlug keys then Some path else None)

    /// Create-if-missing + activity update for the message's channel file.
    let private updateChannel (deps: PipelineDeps) (msg: ChatMessage) (channelSlug: string) (topic: string option) =
        let path = Naming.channelPath channelSlug
        let stub : Channel =
            { Type = "Channel"; Title = msg.NormalizedChatName
              Platform = (if msg.IsGroup then "whatsapp-group" else "whatsapp-direct")
              Context = ""; People = [||]; SignalWeight = "medium"
              MessageCount = 0; LastProcessed = System.DateTime.MinValue; ActiveTopics = [||] }
        let current, body =
            if deps.Vault.Exists path then
                let mf = MarkdownFile.FromString (deps.Vault.Read path)
                match mf.FrontMatter with
                | Some fm -> (Frontmatter.deserialize<Channel> fm), mf.Content
                | None -> stub, mf.Content
            else stub, ""
        let existingTopics = if isNull current.ActiveTopics then [||] else current.ActiveTopics
        let activeTopics =
            match topic with
            | Some t when not (Array.contains t existingTopics) -> Array.append existingTopics [| t |]
            | _ -> existingTopics
        let updated =
            { current with
                LastProcessed = msg.Timestamp
                MessageCount = current.MessageCount + 1
                ActiveTopics = activeTopics }
        deps.Vault.Write(path, MarkdownFile.ToString (Frontmatter.serialize updated) body)

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

    /// Find a collision-free path by inserting -2, -3, ... before the ".md" extension.
    // Precondition: basePath ends in ".md" (all Naming.*Path helpers satisfy this).
    let private freePath (vault: IVault) (basePath: string) =
        if not (vault.Exists basePath) then basePath
        else
            let stem = if basePath.EndsWith(".md") then basePath.[.. basePath.Length - 4] else basePath
            let rec tryN n =
                let candidate = sprintf "%s-%d.md" stem n
                if not (vault.Exists candidate) then candidate else tryN (n + 1)
            tryN 2

    type private EntityOutcome<'T> = { Record: 'T; Body: string }

    type private EntitySpec<'T> =
        { Prompt: string
          BuildUser: string -> string
          Interpret: string -> string -> EntityOutcome<'T>   // stripped reply, intent -> outcome
          BasePath: 'T -> string
          TitleOf: 'T -> string }

    // NOT private: YamlDotNet can't deserialize into a private record (see the
    // fsharp-private-record-serialization lesson) — Title would come back null.
    [<CLIMutable>]
    type TitleOnly = { Title: string }

    /// The slug of an existing entity file's frontmatter title (empty if missing/unparseable).
    let private existingTitleSlug (vault: IVault) (path: string) : string =
        try
            match (MarkdownFile.FromString (vault.Read path)).FrontMatter with
            | Some fm -> Naming.slug (Frontmatter.deserialize<TitleOnly> fm).Title
            | None -> ""
        with _ -> ""

    /// Run a per-type generation prompt for each intent: validate (or fall back),
    /// canonicalize, write, and return the written paths. A same-slug collision with a file
    /// of the same title is a re-extraction of the same entity → overwrite idempotently;
    /// only a genuinely different entity that happens to slug-collide gets a -2 suffix.
    let private writeEntities (deps: PipelineDeps) (spec: EntitySpec<'T>) (intents: string list) : string list =
        intents
        |> List.map (fun intent ->
            let raw = Agent.runConversation deps.Chat [] spec.Prompt (spec.BuildUser intent)
            let outcome = spec.Interpret (stripFences raw) intent
            let text = MarkdownFile.ToString (Frontmatter.serialize outcome.Record) outcome.Body
            let basePath = spec.BasePath outcome.Record
            let newSlug = Naming.slug (spec.TitleOf outcome.Record)
            let path =
                if deps.Vault.Exists basePath && newSlug <> "" && existingTitleSlug deps.Vault basePath = newSlug
                then basePath
                else freePath deps.Vault basePath
            deps.Vault.Write(path, text)
            path)

    /// Shared shortlist-and-confirm core for the match-and-merge sites (notes, tasks, people).
    /// `candidates` are (slug, embedText, displayLine). Embeds `queryText`, scores each candidate's
    /// `embedText` by cosine, keeps those >= `floor`, takes the top `topK`, then asks the model
    /// (via `systemPrompt` over `buildPayload (displayLines)`) to confirm a match and returns the
    /// matched slug. Best-effort: empty candidates / embedder failure / parse error / no confirmed
    /// match all yield None (the caller then creates/stubs as before).
    let private shortlistAndConfirm
        (deps: PipelineDeps) (queryText: string)
        (candidates: (string * string * string) list)
        (floor: float) (topK: int) (systemPrompt: string)
        (buildPayload: string list -> string) : string option =
        if List.isEmpty candidates then None
        else
            try
                let q = deps.Embedder.Embed queryText
                let shortlisted =
                    candidates
                    |> List.map (fun (slug, embedText, line) -> slug, line, Similarity.cosine q (deps.Embedder.Embed embedText))
                    |> List.filter (fun (_, _, s) -> s >= floor)
                    |> List.sortByDescending (fun (_, _, s) -> s)
                    |> List.truncate topK
                    |> List.map (fun (slug, line, _) -> slug, line)
                match shortlisted with
                | [] -> None
                | sl ->
                    let payload = buildPayload (sl |> List.map snd)
                    match Prompts.parseTopicMatch (Agent.runConversation deps.Chat [] systemPrompt payload) with
                    | Ok m when m.Match ->
                        let normalized = (if isNull m.TopicSlug then "" else m.TopicSlug).Trim().ToLowerInvariant()
                        sl |> List.tryPick (fun (slug, _) -> if slug.ToLowerInvariant() = normalized then Some slug else None)
                    | _ -> None
            with _ -> None

    /// Append `name` as an alias on the person file at `existingPath`, unless `mentionSlug` is
    /// already known (the title or an existing alias). Best-effort; never throws.
    let private addPersonAlias (vault: IVault) (existingPath: string) (name: string) (mentionSlug: string) =
        try
            let existing = MarkdownFile.FromString (vault.Read existingPath)
            match existing.FrontMatter with
            | Some fm ->
                let ep = Frontmatter.deserialize<Person> fm
                let existingAliases = if isNull ep.Aliases then [||] else ep.Aliases
                let known =
                    (Naming.slug ep.Title :: (existingAliases |> Array.toList |> List.map Naming.slug)) |> Set.ofList
                if not (Set.contains mentionSlug known) then
                    let merged = { ep with Aliases = Array.append existingAliases [| name.Trim() |] }
                    vault.Write(existingPath, MarkdownFile.ToString (Frontmatter.serialize merged) existing.Content)
            | None -> ()
        with _ -> ()

    let processMessage (deps: PipelineDeps) (id: string) (chatJid: string) : PipelineResult =
        match deps.Messages.GetMessage(id, chatJid) with
        | None -> NotFound
        | Some msg ->
            let channelSlug = Naming.slug msg.NormalizedChatName
            let messagePath = Naming.messagePath channelSlug msg.Timestamp

            // Idempotency: one file per message; reprocessing is a no-op.
            if deps.Vault.Exists messagePath then
                Skipped
            else

            // --- Step: image/audio messages get vision/transcription before classify ---
            let enrich (mediaType: string) (extract: byte array -> string) (m: ChatMessage) =
                let isTarget =
                    (not (isNull m.MediaType)) && m.MediaType = mediaType
                    && System.String.IsNullOrWhiteSpace m.Content
                if not isTarget then false, m
                else
                    match deps.Messages.GetMediaBytes(id, chatJid) with
                    | Some bytes ->
                        (try
                            match extract bytes with
                            | t when not (System.String.IsNullOrWhiteSpace t) -> true, { m with Content = t }
                            | _ -> false, m
                         with _ -> false, m)
                    | None -> false, m

            let imageDerived, msg = enrich "image" deps.Vision.Describe msg
            let audioDerived, msg = enrich "audio" deps.Transcriber.Transcribe msg
            let mediaHeader =
                if imageDerived then "## Image (vision-extracted)\n"
                elif audioDerived then "## Voice note (transcribed)\n"
                else "## Raw\n"

            // --- Short-circuit: nothing to classify (e.g. caption-less video/document with no
            //     vision/transcription text). Classifying empty input just makes the model chat
            //     back ("Please provide the message..."), which fails to parse. Record as noise
            //     and stop, before any LLM call. ---
            if System.String.IsNullOrWhiteSpace msg.Content then
                let record : Message =
                    { Type = "Message"; Channel = channelSlug; Timestamp = isoTimestamp msg.Timestamp
                      Sender = msg.SenderName; Noise = true; Topic = ""
                      SpawnedTasks = [||]; SpawnedEvents = [||]; SpawnedNotes = [||]; ProcessedBy = deps.Model }
                deps.Vault.Write(messagePath, MarkdownFile.ToString (Frontmatter.serialize record) "")
                updateChannel deps msg channelSlug None
                ProcessedNoise
            else

            // --- Step: pull recent conversation history for context (best-effort) ---
            let recent = try deps.Messages.GetRecent(chatJid, msg.Timestamp, id) with _ -> []
            let historyText = Prompts.renderHistory recent

            // --- Step: classify (tool-enabled, may call get_contexts) ---
            let classifyTools = [ Tools.getContexts deps.Vault; Tools.getPeople deps.Vault; Tools.getRelationships deps.Vault ]
            let classifyReply =
                Agent.runConversation deps.Chat classifyTools Prompts.classifySystem (Prompts.classifyUser historyText msg.Content)
            match Prompts.parseClassification classifyReply with
            | Error e ->
                eprintfn "[classify-error] msg=%s chat=%s: %s\n  raw model output: %s"
                    id chatJid e ((if isNull classifyReply then "<null>" else classifyReply.Trim()).Replace("\n", " "))
                LlmError e
            | Ok classification ->

            if classification.Noise then
                // Minimal message record, then stop.
                let record : Message =
                    { Type = "Message"; Channel = channelSlug; Timestamp = isoTimestamp msg.Timestamp
                      Sender = msg.SenderName; Noise = true; Topic = ""
                      SpawnedTasks = [||]; SpawnedEvents = [||]; SpawnedNotes = [||]; ProcessedBy = deps.Model }
                let noiseBody = if imageDerived || audioDerived then mediaHeader + msg.Content else ""
                deps.Vault.Write(messagePath, MarkdownFile.ToString (Frontmatter.serialize record) noiseBody)
                updateChannel deps msg channelSlug None
                ProcessedNoise
            else

            // People references use the canonical slug form (matching person filenames), not a
            // surface display name, so wikilinks and the relationship graph — which key off slugs
            // — resolve to the right person file. Applied both to pipeline-set people (from the
            // classification) and to people the model emits onto its task/event records.
            let slugifyPeople (a: string array) =
                if isNull a then [||]
                else a |> Array.map Naming.slug |> Array.filter (fun s -> s <> "") |> Array.distinct
            let peopleSlugs = slugifyPeople classification.PeopleMentioned

            // --- Step: topic match (embedding shortlist + LLM confirm; fallback to tool-enabled) ---
            let createNewTopic (title: string) =
                let slug = Naming.slug title
                let topicRecord : Topic =
                    { Type = "Topic"; Title = title; Status = "active"
                      Context = classification.Contexts; Channel = channelSlug
                      People = peopleSlugs
                      FirstSeen = isoTimestamp msg.Timestamp; LastUpdated = isoTimestamp msg.Timestamp
                      SpawnedTasks = [||]; SpawnedEvents = [||]; MessageRefs = [||] }
                let body = "## Current understanding\n\n## Open questions\n\n## Resolved\n"
                deps.Vault.Write(Naming.topicPath slug, MarkdownFile.ToString (Frontmatter.serialize topicRecord) body)
                slug, Naming.topicPath slug

            // Active topics: (slug, title, understanding)
            let activeTopics =
                deps.Vault.ListFiles "topics/active"
                |> List.choose (fun path ->
                    try
                        let mf = MarkdownFile.FromString (deps.Vault.Read path)
                        match mf.FrontMatter with
                        | Some fm ->
                            let t = Frontmatter.deserialize<Topic> fm
                            Some (System.IO.Path.GetFileNameWithoutExtension(path), t.Title, understandingOf mf.Content)
                        | None -> None
                    with _ -> None)

            // Embedding shortlist: Some candidates (possibly empty) when embedding works; None to fall back.
            let shortlist =
                if List.isEmpty activeTopics then None
                else
                    try
                        let intentVec = deps.Embedder.Embed classification.Intent
                        activeTopics
                        |> List.map (fun (slug, title, und) ->
                            let score = Similarity.cosine intentVec (deps.Embedder.Embed (title + "\n" + und))
                            (slug, title, und, score))
                        |> List.filter (fun (_, _, _, s) -> s >= deps.SimilarityFloor)
                        |> List.sortByDescending (fun (_, _, _, s) -> s)
                        |> List.truncate deps.TopK
                        |> Some
                    with _ -> None

            let topicOutcome : Result<string * string, string> =
                match shortlist with
                | Some [] ->
                    // clearly new — skip the LLM topic-match call
                    Ok (createNewTopic (titleFromIntent classification.Intent))
                | Some candidates ->
                    let candidateText =
                        candidates
                        |> List.map (fun (slug, title, und, _) -> sprintf "slug: %s\ntitle: %s\nunderstanding: %s" slug title und)
                        |> String.concat "\n\n"
                    let payload = sprintf "New message intent: %s\n\nCandidate topics:\n%s" classification.Intent candidateText
                    match Prompts.parseTopicMatch (Agent.runConversation deps.Chat [] Prompts.topicMatchSystem payload) with
                    | Error e -> Error e
                    | Ok m ->
                        let normalized = (if isNull m.TopicSlug then "" else m.TopicSlug).Trim().ToLowerInvariant()
                        let matched = candidates |> List.tryFind (fun (s, _, _, _) -> s.ToLowerInvariant() = normalized)
                        match m.Match, matched with
                        | true, Some (slug, _, _, _) -> Ok (slug, Naming.topicPath slug)
                        | _ ->
                            let title = if System.String.IsNullOrWhiteSpace m.NewTopicTitle then titleFromIntent classification.Intent else m.NewTopicTitle
                            Ok (createNewTopic title)
                | None ->
                    // fallback: today's tool-enabled match over all active topics
                    let topicTools = [ Tools.getTopics deps.Vault; Tools.getTopic deps.Vault ]
                    let reply = Agent.runConversation deps.Chat topicTools Prompts.topicMatchSystem (sprintf "New message intent: %s" classification.Intent)
                    match Prompts.parseTopicMatch reply with
                    | Error e -> Error e
                    | Ok m ->
                        if m.Match && not (System.String.IsNullOrWhiteSpace m.TopicSlug) then Ok (m.TopicSlug, Naming.topicPath m.TopicSlug)
                        else Ok (createNewTopic m.NewTopicTitle)

            match topicOutcome with
            | Error e ->
                eprintfn "[topic-match-error] msg=%s chat=%s: %s" id chatJid e
                LlmError e
            | Ok (_, topicPath) ->

            // --- Step: create task files via the shared entity writer ---
            let taskSpec : EntitySpec<Task> =
                { Prompt = Prompts.taskCreateSystem
                  BuildUser =
                    (fun intent ->
                        sprintf "Message intent: %s\nRaw message: %s\nContext(s): %s\nUrgency: %s\nSource message file: %s"
                            intent msg.Content (String.concat ", " classification.Contexts) classification.Urgency messagePath)
                  Interpret =
                    (fun stripped intent ->
                        try
                            let parsed = MarkdownFile.FromString stripped
                            match parsed.FrontMatter with
                            | Some fm ->
                                let t = Frontmatter.deserialize<Task> fm
                                if not (System.String.IsNullOrWhiteSpace t.Title) then
                                    // The pipeline owns identity + linkage; the model often omits them.
                                    { Record = { t with Type = "Task"; Topic = topicPath; SourceMessage = messagePath; People = slugifyPeople t.People }
                                      Body = parsed.Content }
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Task =
                                { Type = "Task"; Title = intent; Status = "pending"
                                  Priority = urgencyToPriority classification.Urgency; Due = ""
                                  Context = classification.Contexts; People = peopleSlugs
                                  Topic = topicPath; SourceMessage = messagePath }
                            { Record = fb; Body = intent })
                  BasePath = (fun t -> Naming.taskPath t.Title)
                  TitleOf = (fun t -> t.Title) }

            let createNewTask (intent: string) : string =
                writeEntities deps taskSpec [ intent ] |> List.head

            let processTask (intent: string) : string =
                // Re-scan pending tasks each call so a task written by an earlier intent in this
                // same message is visible to the next (prevents duplicates).
                let existingTasks =
                    deps.Vault.ListFiles "tasks/pending"
                    |> List.choose (fun path ->
                        try
                            let mf = MarkdownFile.FromString (deps.Vault.Read path)
                            match mf.FrontMatter with
                            | Some fm ->
                                let t = Frontmatter.deserialize<Task> fm
                                let slug = System.IO.Path.GetFileNameWithoutExtension(path)
                                let summary = mf.Content.Trim()
                                Some (slug, t.Title + "\n" + summary, sprintf "slug: %s\ntitle: %s\nsummary: %s" slug t.Title summary)
                            | None -> None
                        with _ -> None)
                let buildPayload lines = sprintf "New task intent: %s\n\nCandidate tasks:\n%s" intent (String.concat "\n\n" lines)
                match shortlistAndConfirm deps intent existingTasks deps.TaskSimilarityFloor deps.TaskTopK Prompts.taskMatchSystem buildPayload with
                | Some slug ->
                    let path = sprintf "tasks/pending/%s.md" slug
                    try
                        let existingRaw = deps.Vault.Read path
                        let existing = MarkdownFile.FromString existingRaw
                        match existing.FrontMatter with
                        | Some fm ->
                            let t = Frontmatter.deserialize<Task> fm
                            let updatedRaw =
                                Agent.runConversation deps.Chat [] Prompts.taskUpdateSystem
                                    (Prompts.taskUpdateUser existingRaw intent msg.Content)
                                |> stripFences
                            // Parse the model's updated task; fall back to old record + raw body on failure.
                            let newRec, newBody =
                                try
                                    let parsed = MarkdownFile.FromString updatedRaw
                                    match parsed.FrontMatter with
                                    | Some nfm -> Frontmatter.deserialize<Task> nfm, parsed.Content
                                    | None -> t, updatedRaw
                                with _ -> t, updatedRaw
                            let prank (p: string) =
                                match (if isNull p then "" else p).ToLowerInvariant() with
                                | "critical" -> 3 | "high" -> 2 | "medium" -> 1 | "low" -> 0 | _ -> -1
                            let mergedDue =
                                if System.String.IsNullOrWhiteSpace t.Due
                                   && not (System.String.IsNullOrWhiteSpace newRec.Due)
                                then newRec.Due else t.Due
                            let mergedPriority =
                                if prank newRec.Priority > prank t.Priority then newRec.Priority else t.Priority
                            let merged =
                                { t with
                                    Due = mergedDue
                                    Priority = mergedPriority
                                    Context = Array.append (if isNull t.Context then [||] else t.Context) classification.Contexts |> Array.distinct
                                    People = Array.append (if isNull t.People then [||] else t.People) peopleSlugs |> Array.distinct
                                    SourceMessage = messagePath }
                            deps.Vault.Write(path, MarkdownFile.ToString (Frontmatter.serialize merged) newBody)
                            path
                        | None -> createNewTask intent
                    with _ -> createNewTask intent
                | None -> createNewTask intent

            let taskPaths = classification.Entities.Tasks |> Array.toList |> List.map processTask

            // --- Step: create event files (date-pathed; undated events fall back to the message date) ---
            let parseWhen (s: string) (fallback: System.DateTime) =
                match System.DateTimeOffset.TryParse(s) with
                | true, dto -> dto.DateTime
                | _ -> fallback

            let eventSpec : EntitySpec<Event> =
                { Prompt = Prompts.eventCreateSystem
                  BuildUser =
                    (fun intent ->
                        sprintf "Event intent: %s\nRaw message: %s\nMessage reference date: %s\nContext(s): %s\nSource message file: %s"
                            intent msg.Content (isoTimestamp msg.Timestamp) (String.concat ", " classification.Contexts) messagePath)
                  Interpret =
                    (fun stripped intent ->
                        let flag = "\n\n_Date inferred from message; please confirm._"
                        let ensureDated (e: Event) (body: string) =
                            match System.DateTimeOffset.TryParse(e.When) with
                            | true, _ -> { Record = e; Body = body }
                            | _ -> { Record = { e with When = isoTimestamp msg.Timestamp }; Body = body + flag }
                        try
                            let parsed = MarkdownFile.FromString stripped
                            match parsed.FrontMatter with
                            | Some fm ->
                                let e = Frontmatter.deserialize<Event> fm
                                if not (System.String.IsNullOrWhiteSpace e.Title) then
                                    ensureDated { e with Type = "Event"; Topic = topicPath; TasksLinked = Array.ofList taskPaths; People = slugifyPeople e.People } parsed.Content
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Event =
                                { Type = "Event"; Title = intent; When = isoTimestamp msg.Timestamp; AllDay = true
                                  Context = classification.Contexts; Location = ""; People = peopleSlugs
                                  Topic = topicPath; TasksLinked = Array.ofList taskPaths; ReminderDaysBefore = 3 }
                            { Record = fb; Body = intent + flag })
                  BasePath = (fun e -> Naming.eventPath (parseWhen e.When msg.Timestamp) e.Title)
                  TitleOf = (fun e -> e.Title) }

            let eventPaths = writeEntities deps eventSpec (List.ofArray classification.Entities.Events)

            // --- Step: create commitment files ---
            let commitmentSpec : EntitySpec<Commitment> =
                { Prompt = Prompts.commitmentCreateSystem
                  BuildUser =
                    (fun intent ->
                        sprintf "Commitment intent: %s\nRaw message: %s\nContext(s): %s\nUrgency: %s\nSource message file: %s"
                            intent msg.Content (String.concat ", " classification.Contexts) classification.Urgency messagePath)
                  Interpret =
                    (fun stripped intent ->
                        try
                            let parsed = MarkdownFile.FromString stripped
                            match parsed.FrontMatter with
                            | Some fm ->
                                let c = Frontmatter.deserialize<Commitment> fm
                                if not (System.String.IsNullOrWhiteSpace c.Title) then
                                    { Record = { c with Type = "Commitment"; Topic = topicPath; SourceMessage = messagePath }
                                      Body = parsed.Content }
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Commitment =
                                { Type = "Commitment"; Title = intent; Status = "unresolved"
                                  Priority = urgencyToPriority classification.Urgency; Due = ""
                                  Context = classification.Contexts; Topic = topicPath
                                  TaskAssigned = ""; EscalateAfterDays = 7; SourceMessage = messagePath }
                            { Record = fb; Body = intent })
                  BasePath = (fun c -> Naming.commitmentPath c.Title)
                  TitleOf = (fun c -> c.Title) }

            let commitmentPaths = writeEntities deps commitmentSpec (List.ofArray classification.Entities.Commitments)
            ignore commitmentPaths

            // --- Step: notes (durable reference only) — match an existing note and merge, else create ---
            let interpretNote (stripped: string) (intent: string) : Note * string =
                try
                    let parsed = MarkdownFile.FromString stripped
                    match parsed.FrontMatter with
                    | Some fm ->
                        let n = Frontmatter.deserialize<Note> fm
                        // People linkage is pipeline-owned (slugged from the classification), like
                        // Source — the model is unreliable here and sometimes emits tags or display
                        // names into people_linked.
                        if not (System.String.IsNullOrWhiteSpace n.Title) then { n with Type = "Note"; Source = messagePath; PeopleLinked = peopleSlugs }, parsed.Content
                        else raise (System.Exception("empty title"))
                    | None -> raise (System.Exception("no frontmatter"))
                with _ ->
                    { Type = "Note"; Title = intent; Context = classification.Contexts
                      PeopleLinked = peopleSlugs; Tags = [||]
                      Source = messagePath; LastVerified = "" }, intent

            let createNewNote (intent: string) : string =
                let raw =
                    Agent.runConversation deps.Chat [] Prompts.noteCreateSystem
                        (sprintf "Note intent: %s\nRaw message: %s\nContext(s): %s\nSource message file: %s"
                            intent msg.Content (String.concat ", " classification.Contexts) messagePath)
                let record, body = interpretNote (stripFences raw) intent
                let text = MarkdownFile.ToString (Frontmatter.serialize record) body
                let path = freePath deps.Vault (Naming.notePath record.Title)
                deps.Vault.Write(path, text)
                path

            let processNote (intent: string) : string =
                // Re-scan the vault on every call so a note written by an earlier intent in this same
                // message is visible when processing the next intent (prevents -2.md duplicates).
                let existingNotes =
                    deps.Vault.ListFiles "notes"
                    |> List.choose (fun path ->
                        try
                            let mf = MarkdownFile.FromString (deps.Vault.Read path)
                            match mf.FrontMatter with
                            | Some fm ->
                                let n = Frontmatter.deserialize<Note> fm
                                let slug = System.IO.Path.GetFileNameWithoutExtension(path)
                                let summary = mf.Content.Trim()
                                Some (slug, n.Title + "\n" + summary, sprintf "slug: %s\ntitle: %s\nsummary: %s" slug n.Title summary)
                            | None -> None
                        with _ -> None)
                let buildPayload lines = sprintf "New note intent: %s\n\nCandidate notes:\n%s" intent (String.concat "\n\n" lines)
                match shortlistAndConfirm deps intent existingNotes deps.NoteSimilarityFloor deps.NoteTopK Prompts.noteMatchSystem buildPayload with
                | Some slug ->
                    let path = sprintf "notes/%s.md" slug
                    try
                        let existing = MarkdownFile.FromString (deps.Vault.Read path)
                        match existing.FrontMatter with
                        | Some fm ->
                            let n = Frontmatter.deserialize<Note> fm
                            let mergedBody =
                                Agent.runConversation deps.Chat [] Prompts.noteUpdateSystem
                                    (Prompts.noteUpdateUser existing.Content intent msg.Content)
                                |> stripFences
                            let merged =
                                { n with
                                    LastVerified = isoTimestamp msg.Timestamp
                                    Context = Array.append (if isNull n.Context then [||] else n.Context) classification.Contexts |> Array.distinct
                                    PeopleLinked = Array.append (if isNull n.PeopleLinked then [||] else n.PeopleLinked) peopleSlugs |> Array.distinct }
                            deps.Vault.Write(path, MarkdownFile.ToString (Frontmatter.serialize merged) mergedBody)
                            path
                        | None -> createNewNote intent
                    with _ -> createNewNote intent
                | None -> createNewNote intent

            let notePaths = classification.Entities.Notes |> Array.toList |> List.map processNote

            // --- Step: resolve mentioned people (alias-aware) and create stubs only when genuinely new ---
            let messageCtx =
                classification.Contexts
                |> Array.tryFind (fun c -> List.contains c knownContexts)
                |> Option.defaultValue "family"
            classification.PeopleMentioned
            |> Array.toList
            |> List.iter (fun name ->
                let index = peopleIndex deps.Vault   // rebuilt per mention so files written earlier in this loop are visible
                let mentionSlug = Naming.slug name
                if System.String.IsNullOrWhiteSpace mentionSlug then ()
                elif (resolvePerson index mentionSlug).IsSome then ()   // already known by name or alias
                else
                    // Fuzzy second chance before creating a stub: shortlist existing people by
                    // embedding similarity and confirm same-person; on match, add this surface
                    // form as an alias instead of creating a duplicate stub.
                    let existingPeople =
                        deps.Vault.ListFilesRecursive "people"
                        |> List.choose (fun path ->
                            try
                                let mf = MarkdownFile.FromString (deps.Vault.Read path)
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
                            name (String.concat ", " classification.Contexts) (String.concat "\n\n" lines)
                    let queryText = sprintf "%s\n%s" name (String.concat ", " classification.Contexts)
                    match shortlistAndConfirm deps queryText existingPeople deps.PeopleSimilarityFloor deps.PeopleTopK Prompts.personMatchSystem buildPayload with
                    | Some slug ->
                        // resolve the matched person's path from the index and add the alias
                        match resolvePerson index slug with
                        | Some existingPath -> addPersonAlias deps.Vault existingPath name mentionSlug
                        | None -> ()
                    | None ->
                    let user =
                        sprintf "Person mentioned: %s\nMessage context: %s\nMentioned in: %s"
                            name (String.concat ", " classification.Contexts) messagePath
                    let raw = Agent.runConversation deps.Chat [] Prompts.personStubSystem user
                    let record, body =
                        try
                            let parsed = MarkdownFile.FromString (stripFences raw)
                            match parsed.FrontMatter with
                            | Some fm ->
                                let p = Frontmatter.deserialize<Person> fm
                                if not (System.String.IsNullOrWhiteSpace p.Title) then { p with Type = "Person" }, parsed.Content
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            { Type = "Person"; Title = name; Role = ""; Context = [| messageCtx |]
                              Channel = ""; Phone = ""; Email = ""; Tags = [||]; Aliases = [||] },
                            sprintf "%s\n\n⚠ Stub — details to be completed." name
                    let canonicalSlug = Naming.slug record.Title
                    match resolvePerson index canonicalSlug with
                    | Some existingPath ->
                        // Canonical name already exists — record this surface form as an alias instead of duplicating.
                        addPersonAlias deps.Vault existingPath name mentionSlug
                    | None ->
                        // Genuinely new person: file by canonical slug under a role-derived context.
                        let ctx =
                            let candidate =
                                if not (isNull record.Context) && record.Context.Length > 0
                                   && not (System.String.IsNullOrWhiteSpace record.Context.[0])
                                then record.Context.[0] else messageCtx
                            if List.contains candidate knownContexts then candidate else messageCtx
                        // If the surface mention differs from the canonical title, seed it as an alias.
                        let seededAliases =
                            if mentionSlug <> canonicalSlug && not (System.String.IsNullOrWhiteSpace name) then
                                let existing = if isNull record.Aliases then [||] else record.Aliases
                                if existing |> Array.exists (fun a -> Naming.slug a = mentionSlug) then existing
                                else Array.append existing [| name.Trim() |]
                            else (if isNull record.Aliases then [||] else record.Aliases)
                        let finalRecord = { record with Aliases = seededAliases }
                        deps.Vault.Write(Naming.personPath ctx canonicalSlug,
                                         MarkdownFile.ToString (Frontmatter.serialize finalRecord) body))

            // --- Step: extract person-to-person relationships among resolved, co-mentioned people ---
            (try
                let relIndex = peopleIndex deps.Vault
                let resolve (s: string) = resolvePerson relIndex s
                let resolved =
                    classification.PeopleMentioned
                    |> Array.toList
                    |> List.choose (fun name ->
                        let s = Naming.slug name
                        match resolve s with Some _ -> Some s | None -> None)
                    |> List.distinct
                if List.length resolved >= 2 then
                    let user =
                        sprintf "People mentioned (use these exact slugs for from/to): %s\nMessage: %s"
                            (String.concat ", " resolved) msg.Content
                    match Prompts.parseRelationships
                              (Agent.runConversation deps.Chat [] Prompts.relationshipExtractSystem user) with
                    | Ok extraction when not (isNull extraction.Relationships) ->
                        for edge in extraction.Relationships do
                            match Relationships.buildEdge resolve messagePath edge with
                            | Some rel when Relationships.confidenceRank rel.Confidence >= 1 ->
                                let path = Naming.relationshipPath rel.People.[0] rel.People.[1]
                                let existing =
                                    if deps.Vault.Exists path then
                                        try
                                            (MarkdownFile.FromString(deps.Vault.Read path)).FrontMatter
                                            |> Option.map Frontmatter.deserialize<Relationship>
                                        with _ -> None
                                    else None
                                match Relationships.reconcile existing rel with
                                | Some toWrite ->
                                    let body =
                                        if System.String.IsNullOrWhiteSpace toWrite.Descriptor then toWrite.Title
                                        else toWrite.Descriptor
                                    deps.Vault.Write(path, MarkdownFile.ToString (Frontmatter.serialize toWrite) body)
                                | None -> ()
                            | _ -> ()
                    | _ -> ()
             with _ -> ())

            // --- Step: write the message record referencing topic + tasks ---
            let messageRecord : Message =
                { Type = "Message"; Channel = channelSlug; Timestamp = isoTimestamp msg.Timestamp
                  Sender = msg.SenderName; Noise = false; Topic = topicPath
                  SpawnedTasks = Array.ofList taskPaths; SpawnedEvents = Array.ofList eventPaths; SpawnedNotes = Array.ofList notePaths; ProcessedBy = deps.Model }
            let rawBody = mediaHeader + msg.Content
            deps.Vault.Write(messagePath, MarkdownFile.ToString (Frontmatter.serialize messageRecord) rawBody)

            // --- Step: update the topic body (best-effort; logged warning on failure) ---
            (try
                let existing = MarkdownFile.FromString (deps.Vault.Read topicPath)
                let user = Prompts.topicUpdateUser historyText existing.Content msg.Content classification.Intent
                let newBody = Agent.runConversation deps.Chat [] Prompts.topicUpdateSystem user
                match existing.FrontMatter with
                | Some fm ->
                    let t = Frontmatter.deserialize<Topic> fm
                    let merged =
                        { t with
                            LastUpdated = laterIso t.LastUpdated msg.Timestamp
                            MessageRefs = Array.append t.MessageRefs [| messagePath |]
                            SpawnedTasks = Array.append t.SpawnedTasks (Array.ofList taskPaths)
                            SpawnedEvents = Array.append t.SpawnedEvents (Array.ofList eventPaths) }
                    let updatedFrontmatter = Frontmatter.serialize merged
                    deps.Vault.Write(topicPath, MarkdownFile.ToString updatedFrontmatter newBody)
                | None ->
                    eprintfn "Topic update skipped for %s: no frontmatter found; file left unchanged" topicPath
             with ex ->
                eprintfn "Topic update failed for %s (message already written): %s" topicPath ex.Message)

            updateChannel deps msg channelSlug (Some topicPath)
            Processed(topicPath, taskPaths)
