namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Ports

module Pipeline =

    type PipelineDeps =
        { Messages: IMessageSource
          Vault: IVault
          Chat: IChatClient
          Model: string }

    type PipelineResult =
        | NotFound
        | Skipped
        | ProcessedNoise
        | Processed of topic: string * tasks: string list
        | LlmError of string

    let private isoTimestamp (ts: System.DateTime) = ts.ToString("yyyy-MM-ddTHH:mm:sszzz")

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

    /// True if a person file for this slug exists under any known context directory.
    let private personExists (vault: IVault) (personSlug: string) =
        knownContexts |> List.exists (fun ctx -> vault.Exists(Naming.personPath ctx personSlug))

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
          BasePath: 'T -> string }

    /// Run a per-type generation prompt for each intent: validate (or fall back),
    /// canonicalize, collision-guard the path, write, and return the written paths.
    let private writeEntities (deps: PipelineDeps) (spec: EntitySpec<'T>) (intents: string list) : string list =
        intents
        |> List.map (fun intent ->
            let raw = Agent.runConversation deps.Chat [] spec.Prompt (spec.BuildUser intent)
            let outcome = spec.Interpret (stripFences raw) intent
            let text = MarkdownFile.ToString (Frontmatter.serialize outcome.Record) outcome.Body
            let path = freePath deps.Vault (spec.BasePath outcome.Record)
            deps.Vault.Write(path, text)
            path)

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

            // --- Step: classify (tool-enabled, may call get_contexts) ---
            let classifyTools = [ Tools.getContexts deps.Vault ]
            let classifyReply =
                Agent.runConversation deps.Chat classifyTools Prompts.classifySystem msg.Content
            match Prompts.parseClassification classifyReply with
            | Error e -> LlmError e
            | Ok classification ->

            if classification.Noise then
                // Minimal message record, then stop.
                let record : Message =
                    { Type = "Message"; Channel = channelSlug; Timestamp = isoTimestamp msg.Timestamp
                      Sender = msg.SenderName; Noise = true; Topic = ""
                      SpawnedTasks = [||]; SpawnedEvents = [||]; SpawnedNotes = [||]; ProcessedBy = deps.Model }
                deps.Vault.Write(messagePath, MarkdownFile.ToString (Frontmatter.serialize record) "")
                updateChannel deps msg channelSlug None
                ProcessedNoise
            else

            // --- Step: topic match (tool-enabled: get_topics / get_topic) ---
            let topicTools = [ Tools.getTopics deps.Vault; Tools.getTopic deps.Vault ]
            let topicReply =
                Agent.runConversation deps.Chat topicTools Prompts.topicMatchSystem
                    (sprintf "New message intent: %s" classification.Intent)
            match Prompts.parseTopicMatch topicReply with
            | Error e -> LlmError e
            | Ok matchResult ->

            let _, topicPath =
                if matchResult.Match && not (System.String.IsNullOrWhiteSpace matchResult.TopicSlug) then
                    matchResult.TopicSlug, Naming.topicPath matchResult.TopicSlug
                else
                    let slug = Naming.slug matchResult.NewTopicTitle
                    let topicRecord : Topic =
                        { Type = "Topic"; Title = matchResult.NewTopicTitle; Status = "active"
                          Context = classification.Contexts; Channel = channelSlug
                          People = classification.PeopleMentioned
                          FirstSeen = isoTimestamp msg.Timestamp; LastUpdated = isoTimestamp msg.Timestamp
                          SpawnedTasks = [||]; SpawnedEvents = [||]; MessageRefs = [||] }
                    let body = "## Current understanding\n\n## Open questions\n\n## Resolved\n"
                    deps.Vault.Write(Naming.topicPath slug, MarkdownFile.ToString (Frontmatter.serialize topicRecord) body)
                    slug, Naming.topicPath slug

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
                                if not (System.String.IsNullOrWhiteSpace t.Title) then { Record = t; Body = parsed.Content }
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Task =
                                { Type = "Task"; Title = intent; Status = "pending"
                                  Priority = urgencyToPriority classification.Urgency; Due = ""
                                  Context = classification.Contexts; People = classification.PeopleMentioned
                                  Topic = topicPath; SourceMessage = messagePath }
                            { Record = fb; Body = intent })
                  BasePath = (fun t -> Naming.taskPath t.Title) }

            let taskPaths = writeEntities deps taskSpec (List.ofArray classification.Entities.Tasks)

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
                                if not (System.String.IsNullOrWhiteSpace e.Title) then ensureDated e parsed.Content
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Event =
                                { Type = "Event"; Title = intent; When = isoTimestamp msg.Timestamp; AllDay = true
                                  Context = classification.Contexts; Location = ""; People = classification.PeopleMentioned
                                  Topic = topicPath; TasksLinked = Array.ofList taskPaths; ReminderDaysBefore = 3 }
                            { Record = fb; Body = intent + flag })
                  BasePath = (fun e -> Naming.eventPath (parseWhen e.When msg.Timestamp) e.Title) }

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
                                if not (System.String.IsNullOrWhiteSpace c.Title) then { Record = c; Body = parsed.Content }
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Commitment =
                                { Type = "Commitment"; Title = intent; Status = "unresolved"
                                  Priority = urgencyToPriority classification.Urgency; Due = ""
                                  Context = classification.Contexts; Topic = topicPath
                                  TaskAssigned = ""; EscalateAfterDays = 7; SourceMessage = messagePath }
                            { Record = fb; Body = intent })
                  BasePath = (fun c -> Naming.commitmentPath c.Title) }

            let commitmentPaths = writeEntities deps commitmentSpec (List.ofArray classification.Entities.Commitments)
            ignore commitmentPaths

            // --- Step: create note files ---
            let noteSpec : EntitySpec<Note> =
                { Prompt = Prompts.noteCreateSystem
                  BuildUser =
                    (fun intent ->
                        sprintf "Note intent: %s\nRaw message: %s\nContext(s): %s\nSource message file: %s"
                            intent msg.Content (String.concat ", " classification.Contexts) messagePath)
                  Interpret =
                    (fun stripped intent ->
                        try
                            let parsed = MarkdownFile.FromString stripped
                            match parsed.FrontMatter with
                            | Some fm ->
                                let n = Frontmatter.deserialize<Note> fm
                                if not (System.String.IsNullOrWhiteSpace n.Title) then { Record = n; Body = parsed.Content }
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Note =
                                { Type = "Note"; Title = intent; Context = classification.Contexts
                                  PeopleLinked = classification.PeopleMentioned; Tags = [||]
                                  Source = messagePath; LastVerified = "" }
                            { Record = fb; Body = intent })
                  BasePath = (fun n -> Naming.notePath n.Title) }

            let notePaths = writeEntities deps noteSpec (List.ofArray classification.Entities.Notes)

            // --- Step: create Person-stub files for mentioned people not already in the vault ---
            classification.PeopleMentioned
            |> Array.toList
            |> List.iter (fun name ->
                let personSlug = Naming.slug name
                if not (System.String.IsNullOrWhiteSpace personSlug) && not (personExists deps.Vault personSlug) then
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
                                if not (System.String.IsNullOrWhiteSpace p.Title) then p, parsed.Content
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            { Type = "Person"; Title = name; Role = ""; Context = [| "family" |]
                              Channel = ""; Phone = ""; Email = ""; Tags = [||] },
                            sprintf "%s\n\n⚠ Stub — details to be completed." name
                    let ctx =
                        let candidate =
                            if not (isNull record.Context) && record.Context.Length > 0
                               && not (System.String.IsNullOrWhiteSpace record.Context.[0])
                            then record.Context.[0] else "family"
                        if List.contains candidate knownContexts then candidate else "family"
                    deps.Vault.Write(Naming.personPath ctx personSlug,
                                     MarkdownFile.ToString (Frontmatter.serialize record) body))

            // --- Step: write the message record referencing topic + tasks ---
            let messageRecord : Message =
                { Type = "Message"; Channel = channelSlug; Timestamp = isoTimestamp msg.Timestamp
                  Sender = msg.SenderName; Noise = false; Topic = topicPath
                  SpawnedTasks = Array.ofList taskPaths; SpawnedEvents = Array.ofList eventPaths; SpawnedNotes = Array.ofList notePaths; ProcessedBy = deps.Model }
            deps.Vault.Write(messagePath, MarkdownFile.ToString (Frontmatter.serialize messageRecord) ("## Raw\n" + msg.Content))

            // --- Step: update the topic body (best-effort; logged warning on failure) ---
            (try
                let existing = MarkdownFile.FromString (deps.Vault.Read topicPath)
                let user =
                    sprintf "Current topic body:\n%s\n\nNew message raw text:\n%s\n\nExtracted intent:\n%s"
                        existing.Content msg.Content classification.Intent
                let newBody = Agent.runConversation deps.Chat [] Prompts.topicUpdateSystem user
                match existing.FrontMatter with
                | Some fm ->
                    let t = Frontmatter.deserialize<Topic> fm
                    let merged =
                        { t with
                            LastUpdated = isoTimestamp msg.Timestamp
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
