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

    /// Find a collision-free path by inserting -2, -3, ... before the ".md" extension.
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

            // --- Step: write the message record referencing topic + tasks ---
            let messageRecord : Message =
                { Type = "Message"; Channel = channelSlug; Timestamp = isoTimestamp msg.Timestamp
                  Sender = msg.SenderName; Noise = false; Topic = topicPath
                  SpawnedTasks = Array.ofList taskPaths; SpawnedEvents = [||]; SpawnedNotes = [||]; ProcessedBy = deps.Model }
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
                            SpawnedTasks = Array.append t.SpawnedTasks (Array.ofList taskPaths) }
                    let updatedFrontmatter = Frontmatter.serialize merged
                    deps.Vault.Write(topicPath, MarkdownFile.ToString updatedFrontmatter newBody)
                | None ->
                    eprintfn "Topic update skipped for %s: no frontmatter found; file left unchanged" topicPath
             with ex ->
                eprintfn "Topic update failed for %s (message already written): %s" topicPath ex.Message)

            Processed(topicPath, taskPaths)
