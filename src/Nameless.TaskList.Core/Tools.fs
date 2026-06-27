namespace Nameless.TaskList.Core

open System.Collections.Generic
open Nameless.TaskList.Core.Conversation
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.KnowledgeBase

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
            (fun _ ->
                vault.ListFiles "topics/active"
                |> List.choose (fun path ->
                    try
                        let mf = MarkdownFile.FromString (vault.Read path)
                        match mf.FrontMatter with
                        | Some fm ->
                            let t = Frontmatter.deserialize<Topic> fm
                            if (if isNull t.Status then "active" else t.Status).Trim().ToLowerInvariant() = "archived" then None
                            else Some (sprintf "slug: %s\ntitle: %s" (System.IO.Path.GetFileNameWithoutExtension path) t.Title)
                        | None -> None
                    with _ -> None)
                |> String.concat "\n\n")

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

    let getRelationships (vault: IVault) : Tool =
        define "get_relationships"
            "List the known relationships for a person, given their slug (e.g. dr-naidoo)."
            (oneStringParam "slug" "The person slug, e.g. dr-naidoo")
            (fun args ->
                let slug = Naming.slug (string args.["slug"])
                let matches =
                    vault.ListFilesRecursive "relationships"
                    |> List.filter (fun p -> not (p.EndsWith("index.md")))
                    |> List.filter (fun p ->
                        try
                            match (MarkdownFile.FromString(vault.Read p)).FrontMatter with
                            | Some fm ->
                                let r = Frontmatter.deserialize<Relationship> fm
                                (not (isNull r.People)) && (r.People |> Array.exists (fun s -> Naming.slug s = slug))
                            | None -> false
                        with _ -> false)
                if List.isEmpty matches then sprintf "No relationships found for '%s'." slug
                else matches |> List.map (fun p -> sprintf "## %s\n%s" p (vault.Read p)) |> String.concat "\n\n")

    let names (tools: Tool list) : Set<string> =
        tools |> List.map (fun t -> t.Definition.Function.Name) |> Set.ofList
