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
