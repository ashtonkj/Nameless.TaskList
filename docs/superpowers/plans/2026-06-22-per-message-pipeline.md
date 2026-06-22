# Per-Message Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the spine of the DESIGN.md per-message pipeline — ingest one WhatsApp message over HTTP, classify it via a local LLM using read-only vault tools, match/create a Topic, create Task files, and write the Message record back into the Obsidian vault.

**Architecture:** Ports & adapters. All logic lives in `Nameless.TaskList.Core` behind three interfaces (`IMessageSource`, `IVault`, `IChatClient`). A deterministic orchestrator (`Pipeline.processMessage`) sequences DESIGN §6.1, but each LLM step runs through a tool-enabled `Agent` loop. Adapters (Npgsql, filesystem, Ollama HTTP) implement the ports; the ASP.NET host composes them behind `POST /messages/process`.

**Tech Stack:** F# / .NET 10, ASP.NET Core (`Microsoft.NET.Sdk.Web`), Markdig + YamlDotNet (markdown/YAML), Npgsql (Postgres), System.Text.Json (Ollama wire + LLM JSON), xUnit (tests).

## Global Constraints

- Target framework: `net10.0` for every project.
- Spec of record: `docs/superpowers/specs/2026-06-22-per-message-pipeline-design.md`. KB conventions are authoritative in `docs/DESIGN.md` (§4 schemas, §7 prompts, §8 naming).
- No secrets in source. Connection string lives in `appsettings.Development.json` under `ConnectionStrings:WhatsApp` (existing .NET convention — overrides the spec's `WhatsApp:ConnectionString`). Ollama config under `Ollama:Url` / `Ollama:Model`; vault under `Vault:Root`. Default model `gemma4:e4b`.
- Writes are create / overwrite-body only — **never delete** vault files.
- The LLM (model) only ever calls **read-only** tools. All file writes are performed by `Pipeline.fs`, never by the model.
- Reprocessing the same message must be a no-op (idempotency via Message-file existence).
- TDD: every code change starts with a failing test. Commit after each task.
- Out of scope this increment: Event/Commitment/Person-stub writers, `Channel.last_processed`, index regeneration, digests.

---

### Task 1: Test project + Core dependencies + solution wiring

**Files:**
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj`
- Create: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`
- Create: `tests/Nameless.TaskList.Core.Tests/Sanity.fs`
- Modify: `Nameless.TaskList.slnx`

**Interfaces:**
- Consumes: nothing.
- Produces: a runnable xUnit project referencing Core; Core gains the `Npgsql` package.

- [ ] **Step 1: Add Npgsql to Core**

In `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj`, add to the existing `<ItemGroup>` of `PackageReference`s:

```xml
      <PackageReference Include="Npgsql" Version="9.0.2" />
```

- [ ] **Step 2: Create the test project file**

Create `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
        <Compile Include="Sanity.fs" />
    </ItemGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
        <PackageReference Include="xunit" Version="2.9.2" />
        <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\src\Nameless.TaskList.Core\Nameless.TaskList.Core.fsproj" />
    </ItemGroup>

</Project>
```

- [ ] **Step 3: Write a sanity test**

Create `tests/Nameless.TaskList.Core.Tests/Sanity.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.Sanity

open Xunit

[<Fact>]
let ``test harness runs`` () =
    Assert.Equal(2, 1 + 1)
```

- [ ] **Step 4: Register the test project in the solution**

In `Nameless.TaskList.slnx`, replace the empty tests folder line `<Folder Name="/tests/" />` with:

```xml
  <Folder Name="/tests/">
    <Project Path="tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj" />
  </Folder>
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`
Expected: PASS, 1 test passed.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj tests/ Nameless.TaskList.slnx
git commit -m "test: scaffold Core test project and add Npgsql"
```

---

### Task 2: Naming / slug helpers

**Files:**
- Modify: `src/Nameless.TaskList.Core/KnowledgeBase.fs` (append a `Naming` module at end of file)
- Create: `tests/Nameless.TaskList.Core.Tests/NamingTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj` (register the new test file)

**Interfaces:**
- Consumes: nothing.
- Produces (module `Nameless.TaskList.Core.KnowledgeBase.Naming`):
  - `slug : string -> string`
  - `messageFileName : DateTime -> string` (e.g. `2026-06-15T14-17-45.md`)
  - `messagePath : channelSlug:string -> ts:DateTime -> string` (e.g. `messages/{channelSlug}/{file}`)
  - `taskPath : title:string -> string` (e.g. `tasks/pending/{verb-slug}.md`)
  - `topicPath : slug:string -> string` (e.g. `topics/active/{slug}.md`)

- [ ] **Step 1: Register the test file in the test project**

In `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`, add **before** `Sanity.fs` in the source `<ItemGroup>`:

```xml
        <Compile Include="NamingTests.fs" />
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/NamingTests.fs`:

```fsharp
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
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~NamingTests"`
Expected: FAIL — `Naming` is not defined / does not compile.

- [ ] **Step 4: Implement the Naming module**

Append to `src/Nameless.TaskList.Core/KnowledgeBase.fs`:

```fsharp
module Naming =
    open System.Text.RegularExpressions

    let slug (input: string) : string =
        let lowered = (if isNull input then "" else input).ToLowerInvariant()
        // Replace any run of non-alphanumeric characters with a single hyphen
        let hyphenated = Regex.Replace(lowered, "[^a-z0-9]+", "-")
        hyphenated.Trim('-')

    let messageFileName (ts: System.DateTime) : string =
        // ISO 8601 but filesystem-safe: colons in the time become hyphens (DESIGN §8)
        sprintf "%04d-%02d-%02dT%02d-%02d-%02d.md" ts.Year ts.Month ts.Day ts.Hour ts.Minute ts.Second

    let messagePath (channelSlug: string) (ts: System.DateTime) : string =
        sprintf "messages/%s/%s" channelSlug (messageFileName ts)

    let taskPath (title: string) : string =
        sprintf "tasks/pending/%s.md" (slug title)

    let topicPath (topicSlug: string) : string =
        sprintf "topics/active/%s.md" topicSlug
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~NamingTests"`
Expected: PASS, 6 tests passed.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/KnowledgeBase.fs tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add KB naming/slug helpers"
```

---

### Task 3: Domain records + markdown write-side (frontmatter round-trip)

**Files:**
- Modify: `src/Nameless.TaskList.Core/KnowledgeBase.fs` (add records + serializer; place records/serializer **above** the `Naming` module but below the existing `Channel` record)
- Create: `tests/Nameless.TaskList.Core.Tests/CodecTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: existing `MarkdownFile.FromString`.
- Produces:
  - Records `Message`, `Topic`, `Task` (all `[<CLIMutable>]`, fields below).
  - `Frontmatter.serialize : obj -> string` — YamlDotNet, underscored naming, returns YAML text (no fences).
  - `Frontmatter.deserialize<'T> : string -> 'T`.
  - `MarkdownFile.ToString : frontmatterYaml:string -> body:string -> string` — wraps as `---\n{yaml}---\n\n{body}\n`.

Record field definitions (use exactly these — later tasks depend on them):

```fsharp
[<CLIMutable>]
type Task =
    { Type: string          // "Task"
      Title: string
      Status: string        // "pending"
      Priority: string
      Due: string           // ISO date or "" 
      Context: string array
      People: string array
      Topic: string         // topic file path or ""
      SourceMessage: string }

[<CLIMutable>]
type Topic =
    { Type: string          // "Topic"
      Title: string
      Status: string        // "active"
      Context: string array
      Channel: string
      People: string array
      FirstSeen: string
      LastUpdated: string
      SpawnedTasks: string array
      MessageRefs: string array }

[<CLIMutable>]
type Message =
    { Type: string          // "Message"
      Channel: string
      Timestamp: string
      Sender: string
      Noise: bool
      Topic: string
      SpawnedTasks: string array
      ProcessedBy: string }
```

- [ ] **Step 1: Register the test file**

In the test `.fsproj` source `<ItemGroup>`, add after `NamingTests.fs`:

```xml
        <Compile Include="CodecTests.fs" />
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/CodecTests.fs`:

```fsharp
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
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~CodecTests"`
Expected: FAIL — `Frontmatter` / `MarkdownFile.ToString` / records not defined.

- [ ] **Step 4: Add records, serializer, and ToString**

In `src/Nameless.TaskList.Core/KnowledgeBase.fs`, add `open YamlDotNet.Serialization` and `open YamlDotNet.Serialization.NamingConventions` near the top opens. Add the three records (exact definitions above) after the `Channel` record. Then add the `Frontmatter` module and extend `MarkdownFile` with a `ToString` static member:

```fsharp
module Frontmatter =
    let private serializer =
        SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
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
```

Add to the `MarkdownFile` type (as another `static member`):

```fsharp
    static member ToString (frontMatter: string) (body: string) =
        let fm = if frontMatter.EndsWith("\n") then frontMatter else frontMatter + "\n"
        sprintf "---\n%s---\n\n%s\n" fm (body.TrimEnd())
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~CodecTests"`
Expected: PASS, 3 tests passed.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/KnowledgeBase.fs tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add KB domain records and markdown frontmatter codec"
```

---

### Task 4: Conversation wire types (ChatResponse) + Ports

**Files:**
- Modify: `src/Nameless.TaskList.Core/Conversation.fs` (add `SystemMessage`, response types; keep existing types)
- Create: `src/Nameless.TaskList.Core/Ports.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (add `Ports.fs` to compile list **after** `Conversation.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/WireTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: existing `Conversation` message types, `Library.ChatMessage`.
- Produces:
  - In `Conversation`: `SystemMessage` (+ DU case), `ResponseToolCall` (`{ Function: ResponseToolCallFunction }`), `ResponseToolCallFunction` (`{ Name: string; Arguments: Dictionary<string,obj> }`), `ChatResponseMessage` (`{ Content: string; ToolCalls: ResponseToolCall array }` with `tool_calls` mapped), `ChatResponse` (`{ Message: ChatResponseMessage }`), and `parseResponse : string -> ChatResponse`.
  - In `Nameless.TaskList.Core.Ports`: `IMessageSource`, `IVault`, `IChatClient`.

- [ ] **Step 1: Register the test file**

In the test `.fsproj`, add after `CodecTests.fs`:

```xml
        <Compile Include="WireTests.fs" />
```

- [ ] **Step 2: Write the failing test**

Create `tests/Nameless.TaskList.Core.Tests/WireTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.WireTests

open Nameless.TaskList.Core.Conversation
open Xunit

[<Fact>]
let ``parseResponse reads content and tool calls from Ollama shape`` () =
    let json =
        """{"model":"gemma4:e4b","message":{"role":"assistant","content":"",
        "tool_calls":[{"function":{"name":"get_contexts","arguments":{"q":"medical"}}}]},"done":true}"""
    let resp = parseResponse json
    Assert.Equal(1, resp.Message.ToolCalls.Length)
    Assert.Equal("get_contexts", resp.Message.ToolCalls.[0].Function.Name)
    Assert.Equal("medical", string resp.Message.ToolCalls.[0].Function.Arguments.["q"])

[<Fact>]
let ``parseResponse handles final message with no tool calls`` () =
    let json = """{"model":"m","message":{"role":"assistant","content":"hello"},"done":true}"""
    let resp = parseResponse json
    Assert.Equal("hello", resp.Message.Content)
    Assert.True(isNull (box resp.Message.ToolCalls) || resp.Message.ToolCalls.Length = 0)
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~WireTests"`
Expected: FAIL — `parseResponse` / response types not defined.

- [ ] **Step 4: Add response types + SystemMessage to Conversation.fs**

In `src/Nameless.TaskList.Core/Conversation.fs`:

Add a `SystemMessage` type after `UserMessage`:

```fsharp
type SystemMessage =
    {
        Content: string
    }
    member this.Role = "system"
```

Add the `SystemMessage` case to the `Message` DU and its `Value`:

```fsharp
    | SystemMessage of SystemMessage
```
```fsharp
        | SystemMessage sm -> box sm
```

Add response types and parser at the end of the file:

```fsharp
type ResponseToolCallFunction =
    {
        Name: string
        Arguments: System.Collections.Generic.Dictionary<string, obj>
    }

type ResponseToolCall =
    {
        Function: ResponseToolCallFunction
    }

type ChatResponseMessage =
    {
        Content: string
        [<System.Text.Json.Serialization.JsonPropertyName("tool_calls")>]
        ToolCalls: ResponseToolCall array
    }

type ChatResponse =
    {
        Message: ChatResponseMessage
    }

let parseResponse (json: string) : ChatResponse =
    let options = System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
    System.Text.Json.JsonSerializer.Deserialize<ChatResponse>(json, options)
```

- [ ] **Step 5: Create Ports.fs**

Create `src/Nameless.TaskList.Core/Ports.fs`:

```fsharp
namespace Nameless.TaskList.Core.Ports

open System
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Conversation

/// Fetches messages from the WhatsApp store.
type IMessageSource =
    abstract member GetMessage : id: string * chatJid: string -> ChatMessage option
    abstract member GetRecent : chatJid: string * before: DateTime * excludingId: string -> ChatMessage list

/// Reads and writes markdown files relative to a vault root. Never deletes.
type IVault =
    abstract member Exists : relPath: string -> bool
    abstract member Read : relPath: string -> string
    abstract member Write : relPath: string * content: string -> unit
    abstract member ListFiles : relDir: string -> string list

/// One round-trip to the chat model. messages and tools are pre-serialized objects.
type IChatClient =
    abstract member Chat : messages: obj array * tools: obj array -> ChatResponse
```

- [ ] **Step 6: Register Ports.fs in Core compile order**

In `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj`, add `Ports.fs` immediately after the `Conversation.fs` line:

```xml
        <Compile Include="Ports.fs" />
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~WireTests"`
Expected: PASS, 2 tests passed.

- [ ] **Step 8: Commit**

```bash
git add src/Nameless.TaskList.Core tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add Ollama response parsing and Core ports"
```

---

### Task 5: Read-only tools + registry

**Files:**
- Create: `src/Nameless.TaskList.Core/Tools.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (compile after `Ports.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/Fakes.fs` (shared test doubles)
- Create: `tests/Nameless.TaskList.Core.Tests/ToolsTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: `IVault`, `Conversation.ToolDefinition`, `Conversation.ToolFunction`, `Conversation.ToolParameter`.
- Produces (module `Nameless.TaskList.Core.Tools`):
  - Type `Tool = { Definition: ToolDefinition; Handler: System.Collections.Generic.Dictionary<string,obj> -> string }`.
  - `getContexts : IVault -> Tool`, `getTopics : IVault -> Tool`, `getTopic : IVault -> Tool`, `getPeople : IVault -> Tool`.
  - `names : Tool list -> Set<string>` (helper used in tests).
- Produces (in `Fakes.fs`, module `Nameless.TaskList.Core.Tests.Fakes`): `FakeVault` implementing `IVault` over a mutable `Dictionary<string,string>`, with a `.Seed(path, content)` method.

- [ ] **Step 1: Register the new files**

In the test `.fsproj` source `<ItemGroup>`, add `Fakes.fs` **first** (before `NamingTests.fs`) and `ToolsTests.fs` after `WireTests.fs`:

```xml
        <Compile Include="Fakes.fs" />
```
```xml
        <Compile Include="ToolsTests.fs" />
```

- [ ] **Step 2: Create the FakeVault test double**

Create `tests/Nameless.TaskList.Core.Tests/Fakes.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.Fakes

open System.Collections.Generic
open Nameless.TaskList.Core.Ports

type FakeVault() =
    let files = Dictionary<string, string>()
    member _.Files = files
    member _.Seed(path: string, content: string) = files.[path] <- content
    interface IVault with
        member _.Exists(relPath) = files.ContainsKey(relPath)
        member _.Read(relPath) = files.[relPath]
        member _.Write(relPath, content) = files.[relPath] <- content
        member _.ListFiles(relDir) =
            let prefix = relDir.TrimEnd('/') + "/"
            files.Keys |> Seq.filter (fun k -> k.StartsWith(prefix)) |> List.ofSeq
```

- [ ] **Step 3: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/ToolsTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.ToolsTests

open System.Collections.Generic
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

[<Fact>]
let ``getContexts returns content of files under contexts`` () =
    let vault = FakeVault()
    vault.Seed("contexts/family.md", "---\ntype: Context\ntitle: Family\n---\nFamily stuff")
    let tool = Tools.getContexts (vault :> IVault)
    let result = tool.Handler (Dictionary<string, obj>())
    Assert.Contains("Family", result)

[<Fact>]
let ``getTopic returns the requested topic body`` () =
    let vault = FakeVault()
    vault.Seed("topics/active/birthday.md", "---\ntype: Topic\n---\nCake not ordered")
    let tool = Tools.getTopic (vault :> IVault)
    let args = Dictionary<string, obj>()
    args.["slug"] <- box "birthday"
    let result = tool.Handler args
    Assert.Contains("Cake not ordered", result)

[<Fact>]
let ``getContexts exposes a named tool definition`` () =
    let vault = FakeVault()
    let tool = Tools.getContexts (vault :> IVault)
    Assert.Equal("get_contexts", tool.Definition.Function.Name)
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~ToolsTests"`
Expected: FAIL — `Tools` not defined.

- [ ] **Step 5: Implement Tools.fs**

Create `src/Nameless.TaskList.Core/Tools.fs`:

```fsharp
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
```

- [ ] **Step 6: Register Tools.fs in Core compile order**

In `Nameless.TaskList.Core.fsproj`, add after `Ports.fs`:

```xml
        <Compile Include="Tools.fs" />
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~ToolsTests"`
Expected: PASS, 3 tests passed.

- [ ] **Step 8: Commit**

```bash
git add src/Nameless.TaskList.Core tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add read-only vault tools and registry"
```

---

### Task 6: Agent tool-calling loop

**Files:**
- Create: `src/Nameless.TaskList.Core/Agent.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (compile after `Tools.fs`)
- Modify: `tests/Nameless.TaskList.Core.Tests/Fakes.fs` (add `FakeChatClient`)
- Create: `tests/Nameless.TaskList.Core.Tests/AgentTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: `IChatClient`, `Tool`, `Conversation` message/response types.
- Produces (module `Nameless.TaskList.Core.Agent`):
  - `exception AgentError of string`
  - `runConversation : IChatClient -> Tool list -> system: string -> user: string -> string` (returns the final assistant content; raises `AgentError` if it exceeds 5 iterations).
- Produces (in `Fakes.fs`): `FakeChatClient` taking a `ResponseToolCall list` script — constructed from a `ChatResponse list` queue; each `Chat` call dequeues the next scripted `ChatResponse`.

- [ ] **Step 1: Register the test file**

In the test `.fsproj`, add after `ToolsTests.fs`:

```xml
        <Compile Include="AgentTests.fs" />
```

- [ ] **Step 2: Add FakeChatClient to Fakes.fs**

Append to `tests/Nameless.TaskList.Core.Tests/Fakes.fs`:

```fsharp
open Nameless.TaskList.Core.Conversation

/// Returns scripted responses in order. Records how many times Chat was called.
type FakeChatClient(scripted: ChatResponse list) =
    let queue = Queue<ChatResponse>(scripted)
    member val Calls = 0 with get, set
    interface IChatClient with
        member this.Chat(_messages, _tools) =
            this.Calls <- this.Calls + 1
            queue.Dequeue()

module Responses =
    let final (content: string) : ChatResponse =
        { Message = { Content = content; ToolCalls = [||] } }

    let toolCall (name: string) : ChatResponse =
        { Message =
            { Content = ""
              ToolCalls = [| { Function = { Name = name; Arguments = Dictionary<string, obj>() } } |] } }
```

- [ ] **Step 3: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/AgentTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.AgentTests

open System.Collections.Generic
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Conversation
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

let private echoTool (name: string) (output: string) : Tool =
    { Definition = { Function = { Name = name; Description = "test"
                                  Parameters = { Type = "object"; Required = [||]
                                                 Properties = Dictionary<string, ToolProperty>() } } }
      Handler = fun _ -> output }

[<Fact>]
let ``runConversation returns final content when no tool calls`` () =
    let client = FakeChatClient([ Responses.final "the answer" ])
    let result = Agent.runConversation (client :> IChatClient) [] "sys" "user"
    Assert.Equal("the answer", result)
    Assert.Equal(1, client.Calls)

[<Fact>]
let ``runConversation dispatches a tool call then returns final content`` () =
    let client = FakeChatClient([ Responses.toolCall "get_contexts"; Responses.final "done" ])
    let tools = [ echoTool "get_contexts" "context dump" ]
    let result = Agent.runConversation (client :> IChatClient) tools "sys" "user"
    Assert.Equal("done", result)
    Assert.Equal(2, client.Calls)

[<Fact>]
let ``runConversation raises AgentError when it never stops calling tools`` () =
    // Six tool-call responses with no final: must trip the 5-iteration guard.
    let script = List.replicate 6 (Responses.toolCall "get_contexts")
    let client = FakeChatClient(script)
    let tools = [ echoTool "get_contexts" "x" ]
    Assert.Throws<Agent.AgentError>(fun () ->
        Agent.runConversation (client :> IChatClient) tools "sys" "user" |> ignore)
    |> ignore
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~AgentTests"`
Expected: FAIL — `Agent` not defined.

- [ ] **Step 5: Implement Agent.fs**

Create `src/Nameless.TaskList.Core/Agent.fs`:

```fsharp
namespace Nameless.TaskList.Core

open System.Collections.Generic
open Nameless.TaskList.Core.Conversation
open Nameless.TaskList.Core.Ports

module Agent =

    exception AgentError of string

    let private maxIterations = 5

    let runConversation (client: IChatClient) (tools: Tool list) (system: string) (user: string) : string =
        let handlers =
            tools |> List.map (fun t -> t.Definition.Function.Name, t.Handler) |> Map.ofList
        let toolDefs = tools |> List.map (fun t -> box t.Definition) |> Array.ofList

        let messages = ResizeArray<Message>()
        messages.Add(SystemMessage { Content = system })
        messages.Add(UserMessage { Content = user })

        let mutable answer : string option = None
        let mutable iterations = 0

        while answer.IsNone do
            iterations <- iterations + 1
            if iterations > maxIterations then
                raise (AgentError(sprintf "Exceeded %d tool-calling iterations" maxIterations))

            let payload = messages |> Seq.map (fun m -> m.Value) |> Array.ofSeq
            let response = client.Chat(payload, toolDefs)
            let calls = response.Message.ToolCalls

            if isNull (box calls) || calls.Length = 0 then
                answer <- Some response.Message.Content
            else
                // Record the assistant turn, then execute each requested tool.
                let assistantToolCalls =
                    calls
                    |> Array.mapi (fun i c ->
                        { Function = { Index = i; Name = c.Function.Name; Arguments = c.Function.Arguments } })
                messages.Add(AssistantMessage { Content = response.Message.Content; ToolCalls = assistantToolCalls })

                for c in calls do
                    let name = c.Function.Name
                    let output =
                        match Map.tryFind name handlers with
                        | Some handler -> handler c.Function.Arguments
                        | None -> sprintf "Error: unknown tool '%s'" name
                    messages.Add(ToolMessage { ToolName = name; Content = output })

        answer.Value
```

- [ ] **Step 6: Register Agent.fs in Core compile order**

In `Nameless.TaskList.Core.fsproj`, add after `Tools.fs`:

```xml
        <Compile Include="Agent.fs" />
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~AgentTests"`
Expected: PASS, 3 tests passed.

- [ ] **Step 8: Commit**

```bash
git add src/Nameless.TaskList.Core tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add agent tool-calling loop"
```

---

### Task 7: Prompts + classification/topic-match parsing

**Files:**
- Create: `src/Nameless.TaskList.Core/Prompts.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (compile after `Agent.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/ParsingTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: nothing beyond System.Text.Json.
- Produces (module `Nameless.TaskList.Core.Prompts`):
  - String constants `classifySystem`, `topicMatchSystem`, `taskCreateSystem`, `topicUpdateSystem` (verbatim from DESIGN §7.1–7.4).
  - Records `Entities`, `Classification`, `TopicMatch` (snake_case JSON).
  - `parseClassification : string -> Result<Classification, string>`
  - `parseTopicMatch : string -> Result<TopicMatch, string>`
  - `private extractJson : string -> string` (strips ``` fences / leading prose so a chatty model still parses).

- [ ] **Step 1: Register the test file**

In the test `.fsproj`, add after `AgentTests.fs`:

```xml
        <Compile Include="ParsingTests.fs" />
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/ParsingTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.ParsingTests

open Nameless.TaskList.Core
open Xunit

[<Fact>]
let ``parseClassification reads a noise verdict`` () =
    let json = """{"noise":true,"noise_reason":"emoji only","contexts":[],"intent":null,
                   "action_required":false,"urgency":"none","people_mentioned":[],
                   "entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    match Prompts.parseClassification json with
    | Ok c -> Assert.True(c.Noise)
    | Error e -> failwith e

[<Fact>]
let ``parseClassification extracts tasks and tolerates code fences`` () =
    let json = "```json\n" +
               """{"noise":false,"noise_reason":null,"contexts":["family"],""" +
               """"intent":"call venue","action_required":true,"urgency":"high",""" +
               """"people_mentioned":["wife"],"entities":{"tasks":["call Acrobranch"],""" +
               """"events":[],"commitments":[],"notes":[]}}""" + "\n```"
    match Prompts.parseClassification json with
    | Ok c ->
        Assert.False(c.Noise)
        Assert.Equal<string array>([| "family" |], c.Contexts)
        Assert.Equal<string array>([| "call Acrobranch" |], c.Entities.Tasks)
    | Error e -> failwith e

[<Fact>]
let ``parseClassification returns Error on garbage`` () =
    match Prompts.parseClassification "I cannot help with that" with
    | Ok _ -> failwith "expected Error"
    | Error _ -> ()

[<Fact>]
let ``parseTopicMatch reads a match decision`` () =
    let json = """{"match":true,"topic_slug":"birthday","confidence":0.9,
                   "match_reason":"same subject","new_topic_title":null}"""
    match Prompts.parseTopicMatch json with
    | Ok m ->
        Assert.True(m.Match)
        Assert.Equal("birthday", m.TopicSlug)
    | Error e -> failwith e
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~ParsingTests"`
Expected: FAIL — `Prompts` not defined.

- [ ] **Step 4: Implement Prompts.fs**

Create `src/Nameless.TaskList.Core/Prompts.fs`. (Prompt bodies are copied verbatim from `docs/DESIGN.md` §7.1–7.4; templated steps use `{{...}}` placeholders that `Pipeline.fs` fills with `String.Replace`.)

```fsharp
namespace Nameless.TaskList.Core

open System.Text.Json
open System.Text.Json.Serialization

module Prompts =

    [<CLIMutable>]
    type Entities =
        { Tasks: string array
          Events: string array
          Commitments: string array
          Notes: string array }

    [<CLIMutable>]
    type Classification =
        { Noise: bool
          NoiseReason: string
          Contexts: string array
          Intent: string
          ActionRequired: bool
          Urgency: string
          PeopleMentioned: string array
          Entities: Entities }

    [<CLIMutable>]
    type TopicMatch =
        { Match: bool
          TopicSlug: string
          Confidence: float
          MatchReason: string
          NewTopicTitle: string }

    let classifySystem = """You are a personal knowledge base assistant processing incoming messages for a busy professional.
Your job is to classify each message and extract structured information from it.

The KB uses the following contexts: family, medical, school, finance, professional, personal-kb.

For each message, respond ONLY with a JSON object in this exact format:

{
  "noise": true/false,
  "noise_reason": "string or null — brief reason if noise",
  "contexts": ["array of matching contexts, or empty"],
  "intent": "string — one sentence describing what the message is about, or null if noise",
  "action_required": true/false,
  "urgency": "critical/high/medium/low/none",
  "people_mentioned": ["array of person names or roles mentioned"],
  "entities": {
    "tasks": ["brief description of any tasks implied"],
    "events": ["brief description of any events mentioned with dates if present"],
    "commitments": ["brief description of any deadlines or obligations mentioned"],
    "notes": ["any factual information worth storing"]
  }
}

A message is noise if it is:
- A reaction or emoji-only message
- A simple acknowledgement ("ok", "thanks", "👍", "noted")
- Off-topic social chat with no actionable content
- A forwarded joke, meme description, or chain message

If noise is true, all other fields except noise_reason may be null or empty.
Do not add explanation outside the JSON object.
You may call the get_contexts tool to see context definitions before deciding."""

    let topicMatchSystem = """You are a knowledge base assistant. Your job is to decide whether an incoming message
belongs to an existing open topic, or whether it represents a new topic.

You will be given the extracted intent of the new message. You may call the get_topics tool
to list active topics and get_topic to read one in full.

Respond ONLY with a JSON object:

{
  "match": true/false,
  "topic_slug": "slug of matched topic, or null if no match",
  "confidence": 0.0,
  "match_reason": "brief explanation of why this matches or why no match was found",
  "new_topic_title": "if match is false, suggest a concise title for the new topic, else null"
}

Rules:
- Only match if the new message is clearly about the same subject as an existing topic
- A confidence below 0.75 should result in match: false
- Prefer creating a new topic over forcing a weak match
- Do not add explanation outside the JSON object."""

    let taskCreateSystem = """You are creating a task entry for a personal knowledge base.
Generate the YAML frontmatter and a brief body for a new task file.

Rules:
- title: short, actionable, starts with a verb (Book, Call, Pay, Send, Review, etc.)
- status: always "pending" for new tasks
- priority: infer from context and urgency — default to "medium" if unsure
- due: include only if a specific date or timeframe was mentioned; use ISO 8601 date; null if none
- context: array — choose from [family, medical, school, finance, professional, personal-kb]
- people: array of person slugs relevant to the task (use [] if none)
- Body: 1–3 sentences of relevant detail.

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""

    let topicUpdateSystem = """You are updating a personal knowledge base topic document.
You will be given the current topic document body and a new message that has been linked to it.

Rewrite the document body to reflect the new information. Keep it concise.
Preserve the "## Resolved" section — only add to it, never remove.

Rules:
- Rewrite "## Current understanding" to incorporate the new information naturally
- Update "## Open questions" — remove any the message answers, add new ones it raises
- Add newly resolved items to "## Resolved"
- Do not reference the message itself — just update the facts

Respond ONLY with the updated markdown body (no frontmatter, no explanation)."""

    /// Pull the first JSON object out of a possibly-chatty / fenced model reply.
    let private extractJson (raw: string) : string =
        if isNull raw then "" else
        let start = raw.IndexOf('{')
        let stop = raw.LastIndexOf('}')
        if start >= 0 && stop > start then raw.Substring(start, stop - start + 1)
        else raw

    let private options =
        let o = JsonSerializerOptions(JsonSerializerDefaults.Web)
        o.PropertyNamingPolicy <- JsonNamingPolicy.SnakeCaseLower
        o.NumberHandling <- JsonNumberHandling.AllowReadingFromString
        o

    let private tryParse<'T> (raw: string) : Result<'T, string> =
        try
            let json = extractJson raw
            let value = JsonSerializer.Deserialize<'T>(json, options)
            if obj.ReferenceEquals(value, null) then Error "Model returned null"
            else Ok value
        with ex -> Error(sprintf "Failed to parse model JSON: %s" ex.Message)

    let parseClassification (raw: string) : Result<Classification, string> = tryParse<Classification> raw
    let parseTopicMatch (raw: string) : Result<TopicMatch, string> = tryParse<TopicMatch> raw
```

- [ ] **Step 5: Register Prompts.fs in Core compile order**

In `Nameless.TaskList.Core.fsproj`, add after `Agent.fs`:

```xml
        <Compile Include="Prompts.fs" />
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~ParsingTests"`
Expected: PASS, 4 tests passed.

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList.Core tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add LLM prompts and classification/topic-match parsing"
```

---

### Task 8: Pipeline orchestration

**Files:**
- Create: `src/Nameless.TaskList.Core/Pipeline.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (compile after `Prompts.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: `IMessageSource`, `IVault`, `IChatClient`, `Agent.runConversation`, `Tools.*`, `Prompts.*`, `Naming.*`, `Frontmatter.*`, `MarkdownFile`, `Library.ChatMessage`.
- Produces (module `Nameless.TaskList.Core.Pipeline`):
  - `type PipelineDeps = { Messages: IMessageSource; Vault: IVault; Chat: IChatClient; Model: string }`
  - `type PipelineResult = NotFound | Skipped | ProcessedNoise | Processed of topic: string * tasks: string list | LlmError of string`
  - `processMessage : PipelineDeps -> id: string -> chatJid: string -> PipelineResult`

- [ ] **Step 1: Register the test file**

In the test `.fsproj`, add after `ParsingTests.fs`:

```xml
        <Compile Include="PipelineTests.fs" />
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.PipelineTests

open System
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

// IMessageSource fake returning a single configured message.
type FakeMessages(msg: ChatMessage option) =
    interface IMessageSource with
        member _.GetMessage(_id, _jid) = msg
        member _.GetRecent(_jid, _before, _ex) = []

let sampleMessage () : ChatMessage =
    { Id = "M1"; ChatJid = "27800000000@s.whatsapp.net"; ChatName = "Wife"
      NormalizedChatName = "Wife"; IsGroup = false; SenderId = "27800000000"
      SenderName = "Wife"; SenderPushName = "Wife"; SenderSavedName = "Wife"
      SenderBusinessName = null; IsFromMe = false
      Content = "Can you call Acrobranch tomorrow about the 19th?"
      MediaType = null; FileName = null; AlbumId = null; AlbumIndex = None
      Timestamp = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc) }

let deps (messages: IMessageSource) (vault: FakeVault) (chat: IChatClient) : PipelineDeps =
    { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model" }

[<Fact>]
let ``returns NotFound when the message does not exist`` () =
    let d = deps (FakeMessages(None)) (FakeVault()) (FakeChatClient([]))
    Assert.Equal(NotFound, Pipeline.processMessage d "M1" "jid")

[<Fact>]
let ``noise message writes a minimal message file and creates no task`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"ack","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    let result = Pipeline.processMessage d "M1" "jid"
    Assert.Equal(ProcessedNoise, result)
    Assert.True(vault.Files.Keys |> Seq.exists (fun k -> k.StartsWith("messages/")))
    Assert.False(vault.Files.Keys |> Seq.exists (fun k -> k.StartsWith("tasks/")))

[<Fact>]
let ``signal message creates topic, task, and message referencing them`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"call the venue about the party date","action_required":true,"urgency":"high","people_mentioned":["wife"],"entities":{"tasks":["Call Acrobranch to confirm the 19th"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new subject","new_topic_title":"Ethan birthday party 2026"}"""
    let taskFile = Responses.final "---\ntype: Task\ntitle: Call Acrobranch\nstatus: pending\npriority: high\ncontext:\n  - family\n---\nCall the venue."
    let topicBody = Responses.final "## Current understanding\nParty being planned.\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; taskFile; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    let result = Pipeline.processMessage d "M1" "jid"
    match result with
    | Processed(topic, tasks) ->
        Assert.Equal("topics/active/ethan-birthday-party-2026.md", topic)
        Assert.Single(tasks) |> ignore
        Assert.True(vault.Files.ContainsKey(topic))
        Assert.True(vault.Files.ContainsKey(List.head tasks))
        Assert.True(vault.Files.Keys |> Seq.exists (fun k -> k.StartsWith("messages/")))
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``reprocessing an already-written message is a no-op`` () =
    let vault = FakeVault()
    // Pre-seed the message file at the path the pipeline will compute.
    vault.Seed("messages/wife/2026-06-15T14-17-45.md", "---\ntype: Message\n---\n")
    let chat = FakeChatClient([])  // must not be called
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Assert.Equal(Skipped, Pipeline.processMessage d "M1" "jid")
    Assert.Equal(0, chat.Calls)

[<Fact>]
let ``classification parse failure returns LlmError and writes nothing`` () =
    let vault = FakeVault()
    let chat = FakeChatClient([ Responses.final "I cannot help with that" ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | LlmError _ -> Assert.Empty(vault.Files.Keys)
    | other -> failwithf "expected LlmError, got %A" other
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: FAIL — `Pipeline` not defined.

- [ ] **Step 4: Implement Pipeline.fs**

Create `src/Nameless.TaskList.Core/Pipeline.fs`:

```fsharp
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
                    | None -> fm |> ignore; ""
                deps.Vault.Write(topicPath, MarkdownFile.ToString updatedFrontmatter newBody)
             with ex ->
                eprintfn "Topic update failed for %s (message already written): %s" topicPath ex.Message)

            Processed(topicSlug, taskPaths)
```

> **Note for the implementer:** the `| None -> fm |> ignore; ""` branch in the topic-update frontmatter match is unreachable in practice (we always write topics with frontmatter), but F# requires the match to be total. If the compiler flags the unused `fm`, replace that line with `| None -> ""`.

- [ ] **Step 5: Register Pipeline.fs in Core compile order**

In `Nameless.TaskList.Core.fsproj`, add after `Prompts.fs`:

```xml
        <Compile Include="Pipeline.fs" />
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS, 5 tests passed.

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS, all tests green.

- [ ] **Step 8: Commit**

```bash
git add src/Nameless.TaskList.Core tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add per-message pipeline orchestration"
```

---

### Task 9: Adapters (filesystem unit-tested; Postgres/Ollama wired)

**Files:**
- Create: `src/Nameless.TaskList.Core/Adapters.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (compile after `Pipeline.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/VaultTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: `IMessageSource`, `IVault`, `IChatClient`, `Library.Queries`, `Library.ChatMessage`, `Conversation` request/response types.
- Produces (module `Nameless.TaskList.Core.Adapters`):
  - `FileSystemVault(root: string)` implementing `IVault`.
  - `OllamaChatClient(httpClient: HttpClient, url: string, model: string)` implementing `IChatClient`.
  - `PostgresMessageSource(connectionString: string)` implementing `IMessageSource`.

- [ ] **Step 1: Register the test file**

In the test `.fsproj`, add after `PipelineTests.fs`:

```xml
        <Compile Include="VaultTests.fs" />
```

- [ ] **Step 2: Write the failing tests (filesystem vault against a temp dir)**

Create `tests/Nameless.TaskList.Core.Tests/VaultTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.VaultTests

open System.IO
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Xunit

let private tempRoot () =
    let dir = Path.Combine(Path.GetTempPath(), "ntl-vault-" + System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    dir

[<Fact>]
let ``write then read round-trips and creates parent dirs`` () =
    let root = tempRoot ()
    try
        let vault = FileSystemVault(root) :> IVault
        vault.Write("tasks/pending/x.md", "hello")
        Assert.True(vault.Exists("tasks/pending/x.md"))
        Assert.Equal("hello", vault.Read("tasks/pending/x.md"))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``ListFiles returns files directly under a directory`` () =
    let root = tempRoot ()
    try
        let vault = FileSystemVault(root) :> IVault
        vault.Write("contexts/family.md", "a")
        vault.Write("contexts/medical.md", "b")
        let files = vault.ListFiles("contexts") |> List.sort
        Assert.Equal<string list>([ "contexts/family.md"; "contexts/medical.md" ], files)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``Exists is false for missing files`` () =
    let root = tempRoot ()
    try
        let vault = FileSystemVault(root) :> IVault
        Assert.False(vault.Exists("nope.md"))
    finally
        Directory.Delete(root, true)
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~VaultTests"`
Expected: FAIL — `Adapters` / `FileSystemVault` not defined.

- [ ] **Step 4: Implement Adapters.fs**

Create `src/Nameless.TaskList.Core/Adapters.fs`:

```fsharp
namespace Nameless.TaskList.Core

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Text.Json
open Npgsql
open Nameless.TaskList.Core.Conversation
open Nameless.TaskList.Core.Ports

module Adapters =

    // ---- Vault over the local filesystem ----
    type FileSystemVault(root: string) =
        let full (relPath: string) = Path.Combine(root, relPath)
        interface IVault with
            member _.Exists(relPath) = File.Exists(full relPath)
            member _.Read(relPath) = File.ReadAllText(full relPath)
            member _.Write(relPath, content) =
                let path = full relPath
                Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
                File.WriteAllText(path, content)
            member _.ListFiles(relDir) =
                let dir = full relDir
                if Directory.Exists(dir) then
                    Directory.GetFiles(dir)
                    |> Array.map (fun p -> Path.GetRelativePath(root, p).Replace('\\', '/'))
                    |> List.ofArray
                else []

    // ---- Chat client over Ollama ----
    type private OllamaRequest =
        { model: string
          messages: obj array
          tools: obj array
          stream: bool }

    type OllamaChatClient(httpClient: HttpClient, url: string, model: string) =
        interface IChatClient with
            member _.Chat(messages, tools) =
                let body = { model = model; messages = messages; tools = tools; stream = false }
                let mediaType = MediaTypeHeaderValue.Parse("application/json")
                let content = JsonContent.Create(body, mediaType, JsonSerializerOptions(JsonSerializerDefaults.Web))
                let endpoint = url.TrimEnd('/') + "/api/chat"
                let response = httpClient.PostAsync(Uri(endpoint), content).Result
                response.EnsureSuccessStatusCode() |> ignore
                let json = response.Content.ReadAsStringAsync().Result
                parseResponse json

    // ---- Message source over Postgres ----
    let private getStringOrNull (reader: NpgsqlDataReader) (col: string) =
        let ord = reader.GetOrdinal(col)
        if reader.IsDBNull(ord) then null else reader.GetString(ord)

    let private getIntOrNone (reader: NpgsqlDataReader) (col: string) =
        let ord = reader.GetOrdinal(col)
        if reader.IsDBNull(ord) then None else Some(reader.GetInt32(ord))

    let private mapChat (reader: NpgsqlDataReader) : ChatMessage =
        { Id = reader.GetString(reader.GetOrdinal("id"))
          ChatJid = reader.GetString(reader.GetOrdinal("chat_jid"))
          ChatName = getStringOrNull reader "chat_name"
          NormalizedChatName = getStringOrNull reader "normalized_chat_name"
          IsGroup = reader.GetBoolean(reader.GetOrdinal("is_group"))
          SenderId = getStringOrNull reader "sender"
          SenderName = getStringOrNull reader "sender_name"
          SenderPushName = getStringOrNull reader "sender_push_name"
          SenderSavedName = getStringOrNull reader "sender_saved_name"
          SenderBusinessName = getStringOrNull reader "sender_business_name"
          IsFromMe = reader.GetBoolean(reader.GetOrdinal("is_from_me"))
          Content = getStringOrNull reader "content"
          MediaType = getStringOrNull reader "media_type"
          FileName = getStringOrNull reader "filename"
          AlbumId = getStringOrNull reader "album_id"
          AlbumIndex = getIntOrNone reader "album_index"
          Timestamp = reader.GetDateTime(reader.GetOrdinal("timestamp")) }

    type PostgresMessageSource(connectionString: string) =
        let openConnection () =
            let c = new NpgsqlConnection(connectionString)
            c.Open()
            c
        interface IMessageSource with
            member _.GetMessage(id, chatJid) =
                use conn = openConnection ()
                use cmd = conn.CreateCommand()
                cmd.CommandText <- Queries.GetMessageByIdAndChatJid
                cmd.Parameters.AddWithValue("Id", id) |> ignore
                cmd.Parameters.AddWithValue("ChatJid", chatJid) |> ignore
                use reader = cmd.ExecuteReader()
                if reader.Read() then Some(mapChat reader) else None
            member _.GetRecent(chatJid, before, excludingId) =
                use conn = openConnection ()
                use cmd = conn.CreateCommand()
                cmd.CommandText <- Queries.GetPreviousMessagesByChatIdAndJid
                cmd.Parameters.AddWithValue("Id", excludingId) |> ignore
                cmd.Parameters.AddWithValue("ChatJid", chatJid) |> ignore
                cmd.Parameters.AddWithValue("Timestamp", before) |> ignore
                use reader = cmd.ExecuteReader()
                [ while reader.Read() do yield mapChat reader ]
```

> **Note for the implementer:** `Queries.GetMessageByIdAndChatJid` uses named parameters `@Id` / `@ChatJid`; `GetPreviousMessagesByChatIdAndJid` uses `@Id` / `@ChatJid` / `@Timestamp`. Npgsql maps `AddWithValue("Id", ...)` to `@Id`. No SQL changes are needed.

- [ ] **Step 5: Register Adapters.fs in Core compile order**

In `Nameless.TaskList.Core.fsproj`, add after `Pipeline.fs`:

```xml
        <Compile Include="Adapters.fs" />
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~VaultTests"`
Expected: PASS, 3 tests passed.

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList.Core tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add filesystem, Ollama, and Postgres adapters"
```

---

### Task 10: ASP.NET host — config, DI, and the endpoint

**Files:**
- Delete (remove from compile + delete file): `src/Nameless.TaskList/WeatherForecast.fs`, `src/Nameless.TaskList/Controllers/WeatherForecastController.fs`
- Create: `src/Nameless.TaskList/ProcessMessage.fs`
- Modify: `src/Nameless.TaskList/Program.fs`
- Modify: `src/Nameless.TaskList/Nameless.TaskList.fsproj`
- Modify: `src/Nameless.TaskList/appsettings.json` (add `Ollama` + `Vault` sections)
- Modify: `src/Nameless.TaskList/appsettings.Development.json` (add `Ollama` + `Vault`; keep existing `ConnectionStrings`)
- Modify: `.gitignore` (ignore `appsettings.Development.json`)
- Create: `tests/Nameless.TaskList.Tests/Nameless.TaskList.Tests.fsproj`
- Create: `tests/Nameless.TaskList.Tests/EndpointTests.fs`
- Modify: `Nameless.TaskList.slnx`

**Interfaces:**
- Consumes: `Pipeline.processMessage`, `Pipeline.PipelineDeps`, `Pipeline.PipelineResult`, all three adapters.
- Produces: an HTTP route `POST /messages/process` taking `{ id, chatJid }` and returning the status codes in the spec §8.2.

- [ ] **Step 1: Define the request DTO + handler module**

Create `src/Nameless.TaskList/ProcessMessage.fs`:

```fsharp
namespace Nameless.TaskList

open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Nameless.TaskList.Core.Pipeline

[<CLIMutable>]
type ProcessMessageRequest = { Id: string; ChatJid: string }

module ProcessMessageHandler =
    /// Maps a pipeline result to an HTTP result per spec §8.2.
    let toHttp (result: PipelineResult) : IResult =
        match result with
        | NotFound -> Results.NotFound()
        | Skipped -> Results.Ok(box {| skipped = true |})
        | ProcessedNoise -> Results.Ok(box {| noise = true |})
        | Processed(topic, tasks) -> Results.Ok(box {| topic = topic; tasks = tasks |})
        | LlmError msg -> Results.Json({| error = msg |}, statusCode = 502)
```

- [ ] **Step 2: Write a failing endpoint test**

Create the test project `tests/Nameless.TaskList.Tests/Nameless.TaskList.Tests.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
        <Compile Include="EndpointTests.fs" />
    </ItemGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
        <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
        <PackageReference Include="xunit" Version="2.9.2" />
        <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\src\Nameless.TaskList\Nameless.TaskList.fsproj" />
    </ItemGroup>

</Project>
```

Create `tests/Nameless.TaskList.Tests/EndpointTests.fs`. This maps `PipelineResult` values to HTTP using only the pure `toHttp` helper executed through a minimal endpoint, avoiding live Postgres/Ollama:

```fsharp
module Nameless.TaskList.Tests.EndpointTests

open Nameless.TaskList
open Nameless.TaskList.Core.Pipeline
open Microsoft.AspNetCore.Http
open Xunit

// Verifies the result-to-HTTP mapping (spec §8.2) without a live host.
let private statusOf (result: PipelineResult) : int =
    let httpResult = ProcessMessageHandler.toHttp result
    let ctx = DefaultHttpContext()
    (httpResult.ExecuteAsync(ctx)).Wait()
    ctx.Response.StatusCode

[<Fact>]
let ``NotFound maps to 404`` () =
    Assert.Equal(404, statusOf NotFound)

[<Fact>]
let ``Skipped maps to 200`` () =
    Assert.Equal(200, statusOf Skipped)

[<Fact>]
let ``LlmError maps to 502`` () =
    Assert.Equal(502, statusOf (LlmError "bad json"))

[<Fact>]
let ``Processed maps to 200`` () =
    Assert.Equal(200, statusOf (Processed("topics/active/x.md", [ "tasks/pending/y.md" ])))
```

- [ ] **Step 3: Wire ProcessMessage.fs into the web project and drop WeatherForecast**

In `src/Nameless.TaskList/Nameless.TaskList.fsproj`, replace the source `<ItemGroup>` with (note `ProcessMessage.fs` before `Program.fs`, WeatherForecast removed):

```xml
    <ItemGroup>
        <Compile Include="ProcessMessage.fs"/>
        <Compile Include="Program.fs"/>
    </ItemGroup>
```

Delete the files:

```bash
git rm src/Nameless.TaskList/WeatherForecast.fs src/Nameless.TaskList/Controllers/WeatherForecastController.fs
```

- [ ] **Step 4: Run the endpoint test to verify it fails to compile/fails**

Run: `dotnet test tests/Nameless.TaskList.Tests`
Expected: FAIL — `ProcessMessageHandler` referenced before `Program.fs` is updated, or build error from removed controller. (If the build error is the removed `AddControllers` usage, Step 5 fixes it.)

- [ ] **Step 5: Update Program.fs — config, DI, endpoint**

Replace the contents of `src/Nameless.TaskList/Program.fs`:

```fsharp
namespace Nameless.TaskList
#nowarn "20"

open System.Net.Http
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Core.Pipeline

module Program =

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)
        let cfg = builder.Configuration

        // Adapters as singletons behind their ports.
        builder.Services.AddSingleton<IVault>(fun _ ->
            FileSystemVault(cfg.["Vault:Root"]) :> IVault) |> ignore
        builder.Services.AddSingleton<IMessageSource>(fun _ ->
            PostgresMessageSource(cfg.GetConnectionString("WhatsApp")) :> IMessageSource) |> ignore
        builder.Services.AddSingleton<HttpClient>(fun _ -> new HttpClient()) |> ignore
        builder.Services.AddSingleton<IChatClient>(fun sp ->
            let http = sp.GetRequiredService<HttpClient>()
            OllamaChatClient(http, cfg.["Ollama:Url"], cfg.["Ollama:Model"]) :> IChatClient) |> ignore

        let app = builder.Build()

        app.MapPost("/messages/process", System.Func<ProcessMessageRequest, IMessageSource, IVault, IChatClient, Microsoft.AspNetCore.Http.IResult>(
            fun (req: ProcessMessageRequest) (messages: IMessageSource) (vault: IVault) (chat: IChatClient) ->
                let deps =
                    { Messages = messages; Vault = vault; Chat = chat
                      Model = cfg.["Ollama:Model"] }
                Pipeline.processMessage deps req.Id req.ChatJid
                |> ProcessMessageHandler.toHttp)) |> ignore

        app.Run()
        0
```

- [ ] **Step 6: Add config sections**

In `src/Nameless.TaskList/appsettings.json`, add top-level keys (alongside `Logging`/`AllowedHosts`):

```json
  "Ollama": { "Url": "http://localhost:11434", "Model": "gemma4:e4b" },
  "Vault": { "Root": "/data/@documents/Synced-Vault/Knowledge-Base" }
```

In `src/Nameless.TaskList/appsettings.Development.json`, add the same `Ollama` and `Vault` keys (keep the existing `ConnectionStrings:WhatsApp`).

- [ ] **Step 7: Gitignore the dev settings (contains the DB password)**

Append to `.gitignore`:

```
src/Nameless.TaskList/appsettings.Development.json
```

- [ ] **Step 8: Register the web test project in the solution**

In `Nameless.TaskList.slnx`, add inside the `/tests/` folder:

```xml
    <Project Path="tests/Nameless.TaskList.Tests/Nameless.TaskList.Tests.fsproj" />
```

- [ ] **Step 9: Run the endpoint tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Tests`
Expected: PASS, 4 tests passed.

- [ ] **Step 10: Build the whole solution and run all tests**

Run: `dotnet build` then `dotnet test`
Expected: Build succeeds, 0 warnings/0 errors; all tests across both test projects pass.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat: add /messages/process endpoint and compose pipeline in host"
```

---

## Self-Review Notes (for the implementer)

- **Spec coverage:** Tasks 1–10 cover spec §3 (modules), §4 (ports), §5 (tools), §6 (agent), §7 (pipeline spine + write ordering + person-ref handling), §8 (config + endpoint mapping), §9 (error handling: validate-before-write in Task 8; never-delete in adapters; idempotency test in Task 8), §10 (unit tests with fakes; the integration smoke test against live services is intentionally **not** built this increment — see spec §2 out-of-scope).
- **Deferred (do NOT implement here):** Event/Commitment/Person-stub writers, `Channel.last_processed`, index regeneration, digests.
- **Known total-match wart:** the topic-update frontmatter `match` in Task 8 Step 4 has an unreachable `None` branch; the note there tells you how to satisfy the compiler.
- **Model JSON quirk:** Ollama returns `tool_calls`/`arguments` as native JSON (objects), parsed in Task 4; the LLM-content JSON (classification/topic-match) is parsed with snake_case policy in Task 7. These are two different parse paths on purpose.
```
