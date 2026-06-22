# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

This is an F# / .NET 10 solution (`Nameless.TaskList.slnx`).

```bash
# Build everything
dotnet build

# Run the ASP.NET web host (currently only the scaffolded WeatherForecast API)
dotnet run --project src/Nameless.TaskList

# Run the exploratory driver script — this is where the real pipeline logic is prototyped
dotnet fsi src/Nameless.TaskList.Core/TestScript.fsx
```

There is no test project yet (`tests/` is empty). `TestScript.fsx` is the de facto integration harness — it wires the Core modules against the live external dependencies below.

### External dependencies the code expects at runtime
- **PostgreSQL** database named `whatsapp` on `127.0.0.1:5432` — a WhatsApp message store populated by a whatsmeow-based bridge (tables `messages`, `chats`, `whatsmeow_contacts`). Queries live in `Library.fs`.
- **Ollama** at `http://localhost:11434/api/chat` — local LLM for classification/extraction. `Conversation.fs` targets its OpenAI-style tool-calling chat API.
- **An Obsidian vault** on disk (e.g. `/data/@documents/Synced-Vault/Knowledge-Base`) holding the markdown knowledge base. Paths are currently hardcoded in `TestScript.fsx`.

## Architecture

This codebase implements the pipeline described in `docs/DESIGN.md` — read that first. It is a **privacy-first personal knowledge base** that ingests WhatsApp/email messages, uses a local LLM to extract tasks/events/commitments, and stores everything as markdown files with YAML frontmatter (the "Open Knowledge Format"). **Files are the database**; there is no relational store for the KB itself — the graph lives in wikilinks between markdown files.

The data flows in three stages, each backed by one module in `Nameless.TaskList.Core`:

1. **Ingest** (`Library.fs`) — SQL queries (`Queries.GetMessageByIdAndChatJid`, `GetPreviousMessagesByChatIdAndJid`) pull a message and its recent context out of the WhatsApp Postgres DB, normalising sender/chat names via `COALESCE` over the contacts table. Rows map to the `ChatMessage` record.

2. **Knowledge base I/O** (`KnowledgeBase.fs`) — `MarkdownFile.FromString` splits a vault file into YAML frontmatter + markdown body using Markdig's `UseYamlFrontMatter`. The `[<CLIMutable>]` records (`Context`, `Channel`, …) are the deserialization targets for that frontmatter, consumed via YamlDotNet with `UnderscoredNamingConvention` (YAML `priority_weight` → `PriorityWeight`). Each concept type from DESIGN.md §4 becomes one such record; add new ones here.

3. **LLM conversation** (`Conversation.fs`) — models an Ollama chat exchange. The `Message` DU (`UserMessage`/`AssistantMessage`/`ToolMessage`) serializes to Ollama's JSON shape; note `.Role` and `.Type` are computed members (not fields) so they serialize as constants, and `[<JsonPropertyName>]` attributes map `tool_calls`/`tool_name`. `Conversation.Call()` POSTs the message list and returns the raw `HttpResponseMessage` task.

The `Nameless.TaskList` web project (ASP.NET Core, `Microsoft.NET.Sdk.Web`) is still the default template (WeatherForecast). It references Core but does not yet expose the pipeline — treat it as a placeholder host.

## Conventions

- The KB markdown conventions (frontmatter schemas, status/priority enums, file-naming slugs, ISO-8601 dates with `+02:00`, never-delete/append-only rules) are authoritative in `docs/DESIGN.md` §4 and §8. Match them when generating or parsing vault files.
- When deserializing new frontmatter fields, mirror the snake_case YAML key as a PascalCase record field and rely on `UnderscoredNamingConvention` rather than adding explicit attributes.
- JSON sent to Ollama uses `JsonSerializerOptions.Web` (camelCase). Keep message-shape fields as records and protocol constants (`role`, `type`) as computed members.
