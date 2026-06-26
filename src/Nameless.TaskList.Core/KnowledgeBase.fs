namespace Nameless.TaskList.Core.KnowledgeBase

open System
open Markdig
open Markdig.Extensions.Yaml
open Markdig.Syntax
open YamlDotNet.Serialization
open YamlDotNet.Serialization.NamingConventions

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

    static member ToString (frontMatter: string) (body: string) =
        let fm = if frontMatter.EndsWith("\n") then frontMatter else frontMatter + "\n"
        sprintf "---\n%s---\n\n%s\n" fm (body.TrimEnd())
      



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

[<CLIMutable>]
type Task =
    { Type: string
      Title: string
      Status: string
      Priority: string
      Due: string
      Context: string array
      People: string array
      Topic: string
      SourceMessage: string }

[<CLIMutable>]
type Topic =
    { Type: string
      Title: string
      Status: string
      Context: string array
      Channel: string
      People: string array
      FirstSeen: string
      LastUpdated: string
      SpawnedTasks: string array
      SpawnedEvents: string array
      MessageRefs: string array }

[<CLIMutable>]
type Message =
    { Type: string
      Channel: string
      Timestamp: string
      Sender: string
      Noise: bool
      Topic: string
      SpawnedTasks: string array
      SpawnedEvents: string array
      SpawnedNotes: string array
      ProcessedBy: string }

[<CLIMutable>]
type Event =
    { Type: string
      Title: string
      When: string
      AllDay: bool
      Context: string array
      Location: string
      People: string array
      Topic: string
      TasksLinked: string array
      ReminderDaysBefore: int }

[<CLIMutable>]
type Commitment =
    { Type: string
      Title: string
      Status: string
      Priority: string
      Due: string
      Context: string array
      Topic: string
      TaskAssigned: string
      EscalateAfterDays: int
      SourceMessage: string }

[<CLIMutable>]
type Note =
    { Type: string
      Title: string
      Context: string array
      PeopleLinked: string array
      Tags: string array
      Source: string
      LastVerified: string }

[<CLIMutable>]
type Person =
    { Type: string
      Title: string
      Role: string
      Context: string array
      Channel: string
      Phone: string
      Email: string
      Tags: string array
      Aliases: string array }

[<CLIMutable>]
type Relationship =
    { Type: string
      Title: string
      From: string
      To: string
      Relation: string
      Descriptor: string
      Confidence: string
      People: string array
      Source: string }

[<CLIMutable>]
type Digest =
    { Type: string
      Title: string
      Kind: string
      Generated: string }

module Frontmatter =
    let private serializer =
        SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            // F# shares the Array.empty singleton across fields, so two empty-array
            // properties reference the same object; without this YamlDotNet emits
            // `tags: &o0 []` / `aliases: *o0` anchors. We never want anchors in KB frontmatter.
            .DisableAliases()
            .Build()

    let private deserializer =
        DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()

    let serialize (value: obj) : string =
        serializer.Serialize(value)

    let deserialize<'T> (yaml: string) : 'T =
        deserializer.Deserialize<'T>(yaml)

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

    let eventPath (whenTs: System.DateTime) (title: string) : string =
        sprintf "events/%04d/%02d/%s-%04d-%02d-%02d.md"
            whenTs.Year whenTs.Month (slug title) whenTs.Year whenTs.Month whenTs.Day

    let commitmentPath (title: string) : string =
        sprintf "commitments/%s.md" (slug title)

    let notePath (title: string) : string =
        sprintf "notes/%s.md" (slug title)

    let personPath (context: string) (personSlug: string) : string =
        sprintf "people/%s/%s.md" context personSlug

    let relationshipPath (slugA: string) (slugB: string) : string =
        let a, b =
            if System.String.CompareOrdinal(slugA, slugB) <= 0 then slugA, slugB else slugB, slugA
        sprintf "relationships/%s-%s.md" a b

    let channelPath (channelSlug: string) : string =
        sprintf "channels/whatsapp/%s.md" channelSlug

    let digestPath (day: System.DateTime) (kind: string) : string =
        sprintf "digests/%04d-%02d-%02d-%s.md" day.Year day.Month day.Day kind

