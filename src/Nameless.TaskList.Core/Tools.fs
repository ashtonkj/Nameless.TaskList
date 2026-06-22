namespace Nameless.TaskList.Core

open System.Collections.Generic
open Nameless.TaskList.Core.Conversation
open Nameless.TaskList.Core.Ports

type Tool =
    {
        Definition: ToolDefinition
        Handler: Dictionary<string, obj> -> string
    }

module Tools =

    let private noParams () : ToolParameter =
        { Type = "object"; Required = [||]; Properties = Dictionary<string, ToolProperty>() }

    let private oneStringParam (name: string) (desc: string) : ToolParameter =
        let props = Dictionary<string, ToolProperty>()
        props.[name] <- { Type = "string"; Description = desc }
        { Type = "object"; Required = [| name |]; Properties = props }

    let private define (name: string) (desc: string) (parameters: ToolParameter) (handler: Dictionary<string, obj> -> string) : Tool =
        { Definition = { Function = { Name = name; Description = desc; Parameters = parameters } }
          Handler = handler }

    /// Reads every file directly under a vault directory and concatenates the contents.
    let private dumpDir (vault: IVault) (dir: string) : string =
        vault.ListFiles dir
        |> List.map (fun path -> sprintf "## %s\n%s" path (vault.Read path))
        |> String.concat "\n\n"

    let getContexts (vault: IVault) : Tool =
        define "get_contexts"
            "List the defined contexts (family, medical, school, finance, professional) with their priority guidance."
            (noParams ())
            (fun _ -> dumpDir vault "contexts")

    let getTopics (vault: IVault) : Tool =
        define "get_topics"
            "List currently active topics with their titles and current-understanding summaries."
            (noParams ())
            (fun _ -> dumpDir vault "topics/active")

    let getTopic (vault: IVault) : Tool =
        define "get_topic"
            "Get the full body of one topic by its slug."
            (oneStringParam "slug" "The topic slug, e.g. ethan-birthday-party-2026")
            (fun args ->
                let slug = string args.["slug"]
                let path = sprintf "topics/active/%s.md" slug
                if vault.Exists path then vault.Read path else sprintf "No topic found for slug '%s'." slug)

    let getPeople (vault: IVault) : Tool =
        define "get_people"
            "List known people in the knowledge base with their roles."
            (noParams ())
            (fun _ -> dumpDir vault "people")

    let names (tools: Tool list) : Set<string> =
        tools |> List.map (fun t -> t.Definition.Function.Name) |> Set.ofList
