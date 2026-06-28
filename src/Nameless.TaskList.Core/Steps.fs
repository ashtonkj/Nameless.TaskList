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
