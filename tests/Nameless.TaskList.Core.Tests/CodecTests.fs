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

[<Fact>]
let ``Event round-trips through serialize and FromString`` () =
    let original : Event =
        { Type = "Event"; Title = "Sports day"; When = "2026-06-20T09:00:00+02:00"; AllDay = false
          Context = [| "school" |]; Location = "Field"; People = [| "ethan" |]
          Topic = "topics/active/x.md"; TasksLinked = [| "tasks/pending/y.md" |]; ReminderDaysBefore = 3 }
    let file = MarkdownFile.ToString (Frontmatter.serialize original) "Body."
    let back = Frontmatter.deserialize<Event> (MarkdownFile.FromString file).FrontMatter.Value
    Assert.Equal(original.Title, back.Title)
    Assert.Equal(original.When, back.When)
    Assert.Equal<string array>(original.Context, back.Context)

[<Fact>]
let ``Commitment and Note and Person round-trip`` () =
    let c : Commitment =
        { Type = "Commitment"; Title = "Q3 fees"; Status = "unresolved"; Priority = "high"; Due = "2026-07-01"
          Context = [| "finance" |]; Topic = ""; TaskAssigned = ""; EscalateAfterDays = 7; SourceMessage = "messages/x/y.md" }
    let cb = Frontmatter.deserialize<Commitment> (MarkdownFile.FromString (MarkdownFile.ToString (Frontmatter.serialize c) "b")).FrontMatter.Value
    Assert.Equal(7, cb.EscalateAfterDays)

    let n : Note =
        { Type = "Note"; Title = "Allergy"; Context = [| "medical" |]; PeopleLinked = [| "ethan" |]
          Tags = [| "allergy" |]; Source = "messages/x/y.md"; LastVerified = "" }
    let nb = Frontmatter.deserialize<Note> (MarkdownFile.FromString (MarkdownFile.ToString (Frontmatter.serialize n) "b")).FrontMatter.Value
    Assert.Equal<string array>([| "allergy" |], nb.Tags)

    let p : Person =
        { Type = "Person"; Title = "Dr Naidoo"; Role = "Paediatrician"; Context = [| "medical" |]
          Channel = ""; Phone = ""; Email = ""; Tags = [| "paediatrician" |]; Aliases = [| "Naidoo" |] }
    let pb = Frontmatter.deserialize<Person> (MarkdownFile.FromString (MarkdownFile.ToString (Frontmatter.serialize p) "b")).FrontMatter.Value
    Assert.Equal("Paediatrician", pb.Role)

[<Fact>]
let ``Message carries spawned events and notes`` () =
    let m : Message =
        { Type = "Message"; Channel = "wife"; Timestamp = "2026-06-15T14:17:45+02:00"; Sender = "Wife"
          Noise = false; Topic = "topics/active/x.md"; SpawnedTasks = [| "t" |]
          SpawnedEvents = [| "e" |]; SpawnedNotes = [| "n" |]; ProcessedBy = "m" }
    let back = Frontmatter.deserialize<Message> (MarkdownFile.FromString (MarkdownFile.ToString (Frontmatter.serialize m) "b")).FrontMatter.Value
    Assert.Equal<string array>([| "e" |], back.SpawnedEvents)
    Assert.Equal<string array>([| "n" |], back.SpawnedNotes)
