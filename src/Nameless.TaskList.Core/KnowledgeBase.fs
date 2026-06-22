namespace Nameless.TaskList.Core.KnowledgeBase

open System
open Markdig
open Markdig.Extensions.Yaml
open Markdig.Syntax

type MarkdownFile =
    {
        FrontMatter: string option
        Content: string
    }
    static member FromString (input: string) =
        // 1. Configure Markdig pipeline to support YAML Front Matter
        let pipeline = MarkdownPipelineBuilder().UseYamlFrontMatter().Build();

        // 2. Parse the document into an Abstract Syntax Tree (AST)
        let document = Markdown.Parse(input, pipeline)
        
        let frontMatterBlock = document.Descendants<YamlFrontMatterBlock>() |> Seq.tryHead
        let frontMatter =
            match frontMatterBlock with
            | None -> None
            | Some (fm) -> Some(fm.Lines.ToString())
        
        let markdownBodyOnly =
            match frontMatterBlock with
            | None -> input
            | Some(frontMatterBlock) ->
                // 2. Get the character index where the front matter block ends in the original string
                let endOfFrontMatterIndex = frontMatterBlock.Span.End + 1;

                // 3. Slice the original string from that point to the end
                input.Substring(endOfFrontMatterIndex).TrimStart();
        {
            FrontMatter = frontMatter
            Content = markdownBodyOnly
        }
      



[<CLIMutable>]
type Context =
    {
        Type: string
        Title: string
        Description: string
        PriorityWeight: string
        DeadlineSensitive: bool
        EscalationThreshold: int
        Tags: string array
        PeopleLinked: string array
        ParentContext: string
        SubContexts: string array
        ChannelsLinked: string array
        ReminderRule: string
    }
    static member Path = "contexts"
    
[<CLIMutable>]
type Channel =
    {
        Type: string
        Title: string
        Platform: string
        Context: string
        People: string array
        SignalWeight: string
        MessageCount: int
        LastProcessed: DateTime
        ActiveTopics: string array
    }
    static member Path = "channels"

module Naming =
    open System.Text.RegularExpressions

    let slug (input: string) : string =
        let lowered = (if isNull input then "" else input).ToLowerInvariant()
        // Replace spaces with hyphens first to preserve word boundaries
        let spaced = Regex.Replace(lowered, @"\s+", "-")
        // Remove any remaining non-alphanumeric characters except hyphens
        let cleaned = Regex.Replace(spaced, "[^a-z0-9-]", "")
        // Collapse multiple consecutive hyphens into one
        let collapsed = Regex.Replace(cleaned, "-+", "-")
        // Trim hyphens from start and end
        collapsed.Trim('-')

    let messageFileName (ts: System.DateTime) : string =
        // ISO 8601 but filesystem-safe: colons in the time become hyphens (DESIGN §8)
        sprintf "%04d-%02d-%02dT%02d-%02d-%02d.md" ts.Year ts.Month ts.Day ts.Hour ts.Minute ts.Second

    let messagePath (channelSlug: string) (ts: System.DateTime) : string =
        sprintf "messages/%s/%s" channelSlug (messageFileName ts)

    let taskPath (title: string) : string =
        sprintf "tasks/pending/%s.md" (slug title)

    let topicPath (topicSlug: string) : string =
        sprintf "topics/active/%s.md" topicSlug

