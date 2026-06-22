module Nameless.TaskList.Core.Tests.NamingTests

open System
open Nameless.TaskList.Core.KnowledgeBase
open Xunit

[<Fact>]
let ``slug lowercases and hyphenates`` () =
    Assert.Equal("book-ethans-flu-vaccine", Naming.slug "Book Ethan's flu vaccine")

[<Fact>]
let ``slug collapses non-alphanumerics and trims hyphens`` () =
    Assert.Equal("dr-naidoo", Naming.slug "  Dr. Naidoo!! ")

[<Fact>]
let ``messageFileName uses ISO date with hyphenated time`` () =
    let ts = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc)
    Assert.Equal("2026-06-15T14-17-45.md", Naming.messageFileName ts)

[<Fact>]
let ``messagePath nests under channel slug`` () =
    let ts = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc)
    Assert.Equal("messages/wife-direct/2026-06-15T14-17-45.md", Naming.messagePath "wife-direct" ts)

[<Fact>]
let ``taskPath slugs the title under pending`` () =
    Assert.Equal("tasks/pending/book-ethans-flu-vaccine.md", Naming.taskPath "Book Ethan's flu vaccine")

[<Fact>]
let ``topicPath nests under active`` () =
    Assert.Equal("topics/active/ethan-birthday-party-2026.md", Naming.topicPath "ethan-birthday-party-2026")
