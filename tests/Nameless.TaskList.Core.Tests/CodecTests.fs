module Nameless.TaskList.Core.Tests.CodecTests

open Nameless.TaskList.Core.KnowledgeBase
open Xunit

[<Fact>]
let ``serialize emits snake_case keys`` () =
    let task : Task =
        { Type = "Task"; Title = "Book vaccine"; Status = "pending"; Priority = "high"
          Due = "2026-06-30"; Context = [| "medical" |]; People = [| "ethan" |]
          Topic = "topics/active/x.md"; SourceMessage = "messages/x/y.md" }
    let yaml = Frontmatter.serialize task
    Assert.Contains("source_message:", yaml)
    Assert.Contains("title: Book vaccine", yaml)

[<Fact>]
let ``ToString wraps frontmatter and body with fences`` () =
    let file = MarkdownFile.ToString "title: Hello\n" "Body text."
    Assert.StartsWith("---\n", file)
    Assert.Contains("title: Hello", file)
    Assert.Contains("---\n\nBody text.", file)

[<Fact>]
let ``Task round-trips through serialize and FromString`` () =
    let original : Task =
        { Type = "Task"; Title = "Book vaccine"; Status = "pending"; Priority = "high"
          Due = "2026-06-30"; Context = [| "medical"; "family" |]; People = [| "ethan" |]
          Topic = "topics/active/x.md"; SourceMessage = "messages/x/y.md" }
    let file = MarkdownFile.ToString (Frontmatter.serialize original) "Some body."
    let parsed = MarkdownFile.FromString file
    Assert.True(parsed.FrontMatter.IsSome)
    Assert.Equal("Some body.", parsed.Content.TrimEnd())
    let back = Frontmatter.deserialize<Task> parsed.FrontMatter.Value
    Assert.Equal(original.Title, back.Title)
    Assert.Equal<string array>(original.Context, back.Context)
    Assert.Equal(original.SourceMessage, back.SourceMessage)
