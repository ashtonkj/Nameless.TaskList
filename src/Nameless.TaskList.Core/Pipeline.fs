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
        | Logged
        | Processed of topic: string * tasks: string list
        | LlmError of string

    let private isoTimestamp (ts: System.DateTime) = ts.ToString("yyyy-MM-ddTHH:mm:sszzz")

    /// Automated estate gate-access codes ("... TAP exit 19678 valid for 1 exit till ...") are
    /// one-time machine messages — pure noise whichever direction they travel. The model insists
    /// on topic-ing them, so match them by shape and skip classification entirely. Add further
    /// automated-notification shapes here as they surface.
    let private automatedNoiseRegexes =
        let opts = System.Text.RegularExpressions.RegexOptions.IgnoreCase ||| System.Text.RegularExpressions.RegexOptions.Compiled
        [ System.Text.RegularExpressions.Regex(@"\bTAP\s+(?:exit|entry|entrance)\s+\d+\s+valid\s+for\b", opts) ]
    let isAutomatedNoise (content: string) : bool =
        not (System.String.IsNullOrWhiteSpace content)
        && automatedNoiseRegexes |> List.exists (fun r -> r.IsMatch content)

    /// The later of an existing ISO-8601 timestamp and a new message time. Topic
    /// last_updated must never regress when an out-of-order (older) message is matched
    /// into a topic, which would otherwise push last_updated below first_seen.
    let private laterIso (existing: string) (ts: System.DateTime) : string =
        let nu = isoTimestamp ts
        match System.DateTimeOffset.TryParse existing, System.DateTimeOffset.TryParse nu with
        | (true, prev), (true, cur) -> if prev > cur then existing else nu
        | _ -> nu

    /// Wrap whole-word mentions of known people in a body as Obsidian wikilinks
    /// ([[path|Name]]) so the prose itself participates in the KB graph — OKF's core
    /// idea that the graph lives in links between files, not only in frontmatter.
    /// Existing wikilinks are preserved (never double-wrapped); longer names are linked
    /// first so "Kevin Murray" wins over a bare "Kevin". Pure and best-effort.
    let linkifyPeople (mentions: (string * string) list) (body: string) : string =
        // Link one name, but only outside existing [[...]] spans: split keeping the link
        // chunks (Regex.Split with a capturing group), linkify only the non-link segments,
        // then rejoin. This never double-wraps a link added on a prior fold step.
        let linkOne (text: string) (name: string, target: string) =
            let pattern = sprintf @"\b%s\b" (System.Text.RegularExpressions.Regex.Escape name)
            System.Text.RegularExpressions.Regex.Split(text, @"(\[\[[^\]]*\]\])")
            |> Array.map (fun seg ->
                if seg.StartsWith "[[" then seg
                else System.Text.RegularExpressions.Regex.Replace(seg, pattern, fun _ -> sprintf "[[%s|%s]]" target name))
            |> String.concat ""
        if System.String.IsNullOrWhiteSpace body then body
        else
            mentions
            |> List.filter (fun (n, t) -> not (System.String.IsNullOrWhiteSpace n) && not (System.String.IsNullOrWhiteSpace t))
            |> List.distinctBy fst
            |> List.sortByDescending (fun (n, _) -> n.Length)
            |> List.fold linkOne body

    /// Replace any existing "## Linked people" section of a topic body with a freshly
    /// generated wikilink list built from the topic's resolved people (displayName,
    /// path-without-.md). Deterministic and idempotent: the old block is stripped first
    /// so the section never duplicates when the model echoes it back from prior content,
    /// and it always reflects the current people. OKF: a guaranteed in-body link graph,
    /// independent of how the model happens to phrase the prose.
    let withLinkedPeople (people: (string * string) list) (body: string) : string =
        let stripped =
            System.Text.RegularExpressions.Regex.Replace(
                (if isNull body then "" else body),
                @"\n*##\s+Linked people\b.*?(?=\n##\s|\z)", "",
                System.Text.RegularExpressions.RegexOptions.Singleline).TrimEnd()
        let items =
            people
            |> List.filter (fun (n, t) -> not (System.String.IsNullOrWhiteSpace n) && not (System.String.IsNullOrWhiteSpace t))
            |> List.distinctBy snd
            |> List.map (fun (n, t) -> sprintf "- [[%s|%s]]" t n)
        if List.isEmpty items then stripped
        else stripped + "\n\n## Linked people\n\n" + String.concat "\n" items + "\n"

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
        let path = Naming.channelPathFor msg.Platform channelSlug
        let stub : Channel =
            { Type = "Channel"; Title = msg.NormalizedChatName
              Platform = msg.Platform
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

    type private EntitySpec<'T> =
        { Prompt: string
          BuildUser: string -> string
          Interpret: string -> string -> Steps.EntityOutcome<'T>   // stripped reply, intent -> outcome
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
            let outcome = spec.Interpret (Steps.stripFences raw) intent
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
            let messagePath = Naming.messagePathFor msg.Platform channelSlug msg.Timestamp msg.Id

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

            // --- Short-circuit to noise, before any LLM call, when there is nothing worth
            //     classifying: empty input (e.g. a caption-less video — the model would just chat
            //     back "Please provide the message...", which fails to parse), or content matching
            //     a known automated-noise shape (gate access codes the model insists on topic-ing). ---
            if System.String.IsNullOrWhiteSpace msg.Content || isAutomatedNoise msg.Content then
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

            // --- Step: classify (tool-enabled; endearment-strip applied inside) ---
            match Steps.classify deps.Chat deps.Vault historyText msg.Content with
            | Error err ->
                eprintfn "[classify-error] msg=%s chat=%s: %s\n  raw model output: %s"
                    id chatJid err.Message (err.Raw.Trim().Replace("\n", " "))
                LlmError err.Message
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

            if msg.IsBroadcast then
                // Broadcast feeds are one-to-many: log the message, but never thread it into a
                // topic or extract entities from it.
                let record : Message =
                    { Type = "Message"; Channel = channelSlug; Timestamp = isoTimestamp msg.Timestamp
                      Sender = msg.SenderName; Noise = false; Topic = ""
                      SpawnedTasks = [||]; SpawnedEvents = [||]; SpawnedNotes = [||]; ProcessedBy = deps.Model }
                deps.Vault.Write(messagePath, MarkdownFile.ToString (Frontmatter.serialize record) (mediaHeader + msg.Content))
                updateChannel deps msg channelSlug None
                Logged
            else

            // People references use the canonical slug form (matching person filenames), not a
            // surface display name, so wikilinks and the relationship graph — which key off slugs
            // — resolve to the right person file. Applied both to pipeline-set people (from the
            // classification) and to people the model emits onto its task/event records.
            let peopleSlugs = Steps.slugifyPeople classification.PeopleMentioned

            // --- Helper: create a new topic file from a title (used by the topic-match decision below) ---
            let createNewTopic (title: string) =
                let slug = Naming.slug title
                let topicRecord : Topic =
                    { Type = "Topic"; Title = title; Description = classification.Intent; Status = "active"
                      Context = classification.Contexts; Channel = channelSlug
                      People = peopleSlugs
                      FirstSeen = isoTimestamp msg.Timestamp; LastUpdated = isoTimestamp msg.Timestamp
                      SpawnedTasks = [||]; SpawnedEvents = [||]; MessageRefs = [||] }
                let body = "## Current understanding\n\n## Open questions\n\n## Resolved\n"
                deps.Vault.Write(Naming.topicPath slug, MarkdownFile.ToString (Frontmatter.serialize topicRecord) body)
                slug, Naming.topicPath slug

            // --- Step: topic match (shared with the eval harness) ---
            match Steps.matchTopic deps.Chat deps.Embedder deps.Vault deps.TopK deps.SimilarityFloor classification.Intent with
            | Error e ->
                eprintfn "[topic-match-error] msg=%s chat=%s: %s" id chatJid e
                LlmError e
            | Ok decision ->

            let topicPath, isNewTopic =
                match decision with
                | Steps.MatchExisting slug -> Naming.topicPath slug, false
                | Steps.CreateTopic title -> let (_, p) = createNewTopic title in p, true

            // --- Step: create task files via the shared entity writer ---
            let taskSpec : EntitySpec<Task> =
                { Prompt = Prompts.taskCreateSystem
                  BuildUser =
                    (fun intent ->
                        sprintf "Message intent: %s\nRaw message: %s\nMessage reference date (resolve relative dates like \"tomorrow\" against this): %s\nContext(s): %s\nUrgency: %s\nSource message file: %s"
                            intent msg.Content (isoTimestamp msg.Timestamp) (String.concat ", " classification.Contexts) classification.Urgency messagePath)
                  Interpret =
                    (fun stripped intent ->
                        try
                            let parsed = MarkdownFile.FromString stripped
                            match parsed.FrontMatter with
                            | Some fm ->
                                let t = Frontmatter.deserialize<Task> fm
                                if not (System.String.IsNullOrWhiteSpace t.Title) then
                                    // The pipeline owns identity + linkage; the model often omits them.
                                    { Record = { t with Type = "Task"; Description = (if System.String.IsNullOrWhiteSpace t.Description then intent else t.Description); Topic = topicPath; SourceMessage = messagePath; People = Steps.slugifyPeople t.People }
                                      Body = parsed.Content }
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Task =
                                { Type = "Task"; Title = intent; Description = intent; Status = "pending"
                                  Priority = Steps.urgencyToPriority classification.Urgency; Due = ""
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
                                |> Steps.stripFences
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

            // distinct: near-duplicate intents (punctuation/wording wobble) can slugify to the
            // same file, and match-and-merge can fold two intents onto one existing task — either
            // way the same path must appear once in spawned_tasks and the response.
            let taskPaths = classification.Entities.Tasks |> Array.toList |> List.map processTask |> List.distinct

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
                        let ensureDated (e: Event) (body: string) : Steps.EntityOutcome<Event> =
                            match System.DateTimeOffset.TryParse(e.When) with
                            | true, _ -> { Record = e; Body = body }
                            | _ -> { Record = { e with When = isoTimestamp msg.Timestamp }; Body = body + flag }
                        try
                            let parsed = MarkdownFile.FromString stripped
                            match parsed.FrontMatter with
                            | Some fm ->
                                let e = Frontmatter.deserialize<Event> fm
                                if not (System.String.IsNullOrWhiteSpace e.Title) then
                                    ensureDated { e with Type = "Event"; Description = (if System.String.IsNullOrWhiteSpace e.Description then intent else e.Description); Topic = topicPath; TasksLinked = Array.ofList taskPaths; People = Steps.slugifyPeople e.People } parsed.Content
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Event =
                                { Type = "Event"; Title = intent; Description = intent; When = isoTimestamp msg.Timestamp; AllDay = true
                                  Context = classification.Contexts; Location = ""; People = peopleSlugs
                                  Topic = topicPath; TasksLinked = Array.ofList taskPaths; ReminderDaysBefore = 3 }
                            { Record = fb; Body = intent + flag })
                  BasePath = (fun e -> Naming.eventPath (parseWhen e.When msg.Timestamp) e.Title)
                  TitleOf = (fun e -> e.Title) }

            let eventPaths = writeEntities deps eventSpec (List.ofArray classification.Entities.Events) |> List.distinct

            // --- Step: create commitment files ---
            let commitmentSpec : EntitySpec<Commitment> =
                { Prompt = Prompts.commitmentCreateSystem
                  BuildUser =
                    (fun intent ->
                        sprintf "Commitment intent: %s\nRaw message: %s\nReference date (resolve relative dates against this): %s\nContext(s): %s\nUrgency: %s\nSource message file: %s"
                            intent msg.Content (isoTimestamp msg.Timestamp) (String.concat ", " classification.Contexts) classification.Urgency messagePath)
                  Interpret =
                    (fun stripped intent ->
                        try
                            let parsed = MarkdownFile.FromString stripped
                            match parsed.FrontMatter with
                            | Some fm ->
                                let c = Frontmatter.deserialize<Commitment> fm
                                if not (System.String.IsNullOrWhiteSpace c.Title) then
                                    { Record = { c with Type = "Commitment"; Description = (if System.String.IsNullOrWhiteSpace c.Description then intent else c.Description); Topic = topicPath; SourceMessage = messagePath }
                                      Body = parsed.Content }
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Commitment =
                                { Type = "Commitment"; Title = intent; Description = intent; Status = "unresolved"
                                  Priority = Steps.urgencyToPriority classification.Urgency; Due = ""
                                  Context = classification.Contexts; Topic = topicPath
                                  TaskAssigned = ""; EscalateAfterDays = 7; SourceMessage = messagePath }
                            { Record = fb; Body = intent })
                  BasePath = (fun c -> Naming.commitmentPath c.Title)
                  TitleOf = (fun c -> c.Title) }

            let commitmentPaths = writeEntities deps commitmentSpec (List.ofArray classification.Entities.Commitments) |> List.distinct

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
                        if not (System.String.IsNullOrWhiteSpace n.Title) then { n with Type = "Note"; Description = (if System.String.IsNullOrWhiteSpace n.Description then intent else n.Description); Source = messagePath; PeopleLinked = peopleSlugs }, parsed.Content
                        else raise (System.Exception("empty title"))
                    | None -> raise (System.Exception("no frontmatter"))
                with _ ->
                    { Type = "Note"; Title = intent; Description = intent; Context = classification.Contexts
                      PeopleLinked = peopleSlugs; Tags = [||]
                      Source = messagePath; LastVerified = "" }, intent

            let createNewNote (intent: string) : string =
                let raw =
                    Agent.runConversation deps.Chat [] Prompts.noteCreateSystem
                        (sprintf "Note intent: %s\nRaw message: %s\nContext(s): %s\nSource message file: %s"
                            intent msg.Content (String.concat ", " classification.Contexts) messagePath)
                let record, body = interpretNote (Steps.stripFences raw) intent
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
                                |> Steps.stripFences
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

            let notePaths = classification.Entities.Notes |> Array.toList |> List.map processNote |> List.distinct

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
                            let parsed = MarkdownFile.FromString (Steps.stripFences raw)
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

            // --- Step: wikilink resolved people into the entity bodies. Task/commitment/
            // note/event prose reliably names the people involved, and by now every mention
            // is resolved to a person file, so the links are correct. OKF: the graph lives
            // in body links, not only frontmatter. Best-effort; never blocks the write.
            (try
                let idx = peopleIndex deps.Vault
                let mentionLinks =
                    classification.PeopleMentioned
                    |> Array.toList
                    |> List.choose (fun name ->
                        match resolvePerson idx (Naming.slug name) with
                        | Some path -> Some (name.Trim(), (if path.EndsWith ".md" then path.Substring(0, path.Length - 3) else path))
                        | None -> None)
                if not (List.isEmpty mentionLinks) then
                    for path in (taskPaths @ eventPaths @ commitmentPaths @ notePaths) do
                        if deps.Vault.Exists path then
                            let mf = MarkdownFile.FromString (deps.Vault.Read path)
                            let linked = linkifyPeople mentionLinks mf.Content
                            if linked <> mf.Content then
                                match mf.FrontMatter with
                                | Some fm -> deps.Vault.Write(path, MarkdownFile.ToString fm linked)
                                | None -> ()
             with ex -> eprintfn "Entity wikilinking skipped: %s" ex.Message)

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
                // Deliberately NOT passing conversation history here: the topic body must
                // reflect only the topic's own messages. Capable models (granite, 12b) treat
                // a history block as source material and copy unrelated facts from adjacent
                // messages into the body. The new message's own content (msg.Content) still
                // flows in directly; history remains available to the classify step, where it
                // disambiguates meaning without polluting stored prose.
                let user = Prompts.topicUpdateUser "" existing.Content msg.Content classification.Intent
                let resolved, newBody = Prompts.parseTopicUpdate (Agent.runConversation deps.Chat [] Prompts.topicUpdateSystem user)
                match existing.FrontMatter with
                | Some fm ->
                    let t = Frontmatter.deserialize<Topic> fm
                    // New topics are always active; a matched topic is resolved only when the
                    // model said so this turn, otherwise active (re-activating a resolved one).
                    let newStatus = if isNewTopic then "active" elif resolved then "resolved" else "active"
                    let merged =
                        { t with
                            Status = newStatus
                            LastUpdated = laterIso t.LastUpdated msg.Timestamp
                            MessageRefs = Array.append (if isNull t.MessageRefs then [||] else t.MessageRefs) [| messagePath |]
                            SpawnedTasks = Array.append (if isNull t.SpawnedTasks then [||] else t.SpawnedTasks) (Array.ofList taskPaths)
                            SpawnedEvents = Array.append (if isNull t.SpawnedEvents then [||] else t.SpawnedEvents) (Array.ofList eventPaths) }
                    // Resolve every person on the topic (title + path) once, then use that
                    // list both to wikilink their names inline throughout the body and to
                    // append a deterministic "## Linked people" section. Linking against the
                    // topic's people (not just the current message's mentions) keeps the body
                    // consistently linked across updates; the section guarantees the edge even
                    // when the model paraphrases names out of the prose entirely. OKF: the
                    // graph lives in body links.
                    let linkedPeople =
                        let idx = peopleIndex deps.Vault
                        (if isNull merged.People then [||] else merged.People)
                        |> Array.toList
                        |> List.choose (fun slug ->
                            match resolvePerson idx slug with
                            | Some path ->
                                let title =
                                    try
                                        match (MarkdownFile.FromString (deps.Vault.Read path)).FrontMatter with
                                        | Some pfm -> (Frontmatter.deserialize<Person> pfm).Title
                                        | None -> slug
                                    with _ -> slug
                                let display = if System.String.IsNullOrWhiteSpace title then slug else title
                                let pathNoMd = if path.EndsWith ".md" then path.Substring(0, path.Length - 3) else path
                                Some (display, pathNoMd)
                            | None -> None)
                    let finalBody = withLinkedPeople linkedPeople (linkifyPeople linkedPeople newBody)
                    let updatedFrontmatter = Frontmatter.serialize merged
                    deps.Vault.Write(topicPath, MarkdownFile.ToString updatedFrontmatter finalBody)
                | None ->
                    eprintfn "Topic update skipped for %s: no frontmatter found; file left unchanged" topicPath
             with ex ->
                eprintfn "Topic update failed for %s (message already written): %s" topicPath ex.Message)

            updateChannel deps msg channelSlug (Some topicPath)
            Processed(topicPath, taskPaths)
