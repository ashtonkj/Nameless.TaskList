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
                      SpawnedTasks = [||]; ProcessedBy = deps.Model }
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

            let topicSlug, topicPath =
                if matchResult.Match && not (System.String.IsNullOrWhiteSpace matchResult.TopicSlug) then
                    matchResult.TopicSlug, Naming.topicPath matchResult.TopicSlug
                else
                    let slug = Naming.slug matchResult.NewTopicTitle
                    let topicRecord : Topic =
                        { Type = "Topic"; Title = matchResult.NewTopicTitle; Status = "active"
                          Context = classification.Contexts; Channel = channelSlug
                          People = classification.PeopleMentioned
                          FirstSeen = isoTimestamp msg.Timestamp; LastUpdated = isoTimestamp msg.Timestamp
                          SpawnedTasks = [||]; MessageRefs = [||] }
                    let body = "## Current understanding\n\n## Open questions\n\n## Resolved\n"
                    deps.Vault.Write(Naming.topicPath slug, MarkdownFile.ToString (Frontmatter.serialize topicRecord) body)
                    slug, Naming.topicPath slug

            // --- Step: create one task file per task intent ---
            let taskPaths =
                classification.Entities.Tasks
                |> Array.toList
                |> List.map (fun taskIntent ->
                    let user =
                        sprintf "Message intent: %s\nRaw message: %s\nContext(s): %s\nUrgency: %s\nSource message file: %s"
                            taskIntent msg.Content (String.concat ", " classification.Contexts) classification.Urgency messagePath
                    let fileText = Agent.runConversation deps.Chat [] Prompts.taskCreateSystem user
                    // Parse the returned markdown to recover the title for the filename.
                    let parsed = MarkdownFile.FromString fileText
                    let title =
                        match parsed.FrontMatter with
                        | Some fm -> (Frontmatter.deserialize<Task> fm).Title
                        | None -> taskIntent
                    let path = Naming.taskPath title
                    deps.Vault.Write(path, fileText)
                    path)

            // --- Step: write the message record referencing topic + tasks ---
            let messageRecord : Message =
                { Type = "Message"; Channel = channelSlug; Timestamp = isoTimestamp msg.Timestamp
                  Sender = msg.SenderName; Noise = false; Topic = topicPath
                  SpawnedTasks = Array.ofList taskPaths; ProcessedBy = deps.Model }
            deps.Vault.Write(messagePath, MarkdownFile.ToString (Frontmatter.serialize messageRecord) ("## Raw\n" + msg.Content))

            // --- Step: update the topic body (best-effort; logged warning on failure) ---
            (try
                let existing = MarkdownFile.FromString (deps.Vault.Read topicPath)
                let user =
                    sprintf "Current topic body:\n%s\n\nNew message raw text:\n%s\n\nExtracted intent:\n%s"
                        existing.Content msg.Content classification.Intent
                let newBody = Agent.runConversation deps.Chat [] Prompts.topicUpdateSystem user
                let updatedFrontmatter =
                    match existing.FrontMatter with
                    | Some fm ->
                        let t = Frontmatter.deserialize<Topic> fm
                        let merged =
                            { t with
                                LastUpdated = isoTimestamp msg.Timestamp
                                MessageRefs = Array.append t.MessageRefs [| messagePath |]
                                SpawnedTasks = Array.append t.SpawnedTasks (Array.ofList taskPaths) }
                        Frontmatter.serialize merged
                    | None -> ""
                deps.Vault.Write(topicPath, MarkdownFile.ToString updatedFrontmatter newBody)
             with ex ->
                eprintfn "Topic update failed for %s (message already written): %s" topicPath ex.Message)

            Processed(topicPath, taskPaths)
