# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

This is an F# / .NET 10 solution (`Nameless.TaskList.slnx`).

```bash
# Build everything
dotnet build

# Run all tests (Core unit tests + endpoint mapping tests; in-memory fakes, no live services needed)
dotnet test

# Run one test class
dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"

# Run the opt-in live-service integration suite (real Postgres/Ollama/Whisper; each test skips when its service is absent).
# Excluded from the default `dotnet test`; requires the -p:Integration=true flag.
dotnet test tests/Nameless.TaskList.IntegrationTests -p:Integration=true

# Run the ASP.NET web host — exposes POST /messages/process { id, chatJid }
dotnet run --project src/Nameless.TaskList

# Exploratory driver script (legacy prototype; not part of the build)
dotnet fsi src/Nameless.TaskList.Core/TestScript.fsx
```

Tests live in `tests/Nameless.TaskList.Core.Tests` (xUnit, in-memory `FakeVault`/`FakeChatClient`/`FakeMessages`) and `tests/Nameless.TaskList.Tests` (endpoint result→HTTP mapping). They need no live Postgres/Ollama/vault. Live-service integration tests live in `tests/Nameless.TaskList.IntegrationTests` and are opt-in (see command above).

### External dependencies the code expects at runtime (not in tests)
- **PostgreSQL** database named `whatsapp` on `127.0.0.1:5432` — a WhatsApp message store populated by a whatsmeow-based bridge (tables `messages`, `chats`, `whatsmeow_contacts`). Queries live in `Library.fs`; the `PostgresMessageSource` adapter runs them.
- **Ollama** at `http://localhost:11434` — local LLM for classification/extraction, called via native tool-calling. `OllamaChatClient` (`Adapters.fs`) POSTs to `/api/chat`.
- **An Obsidian vault** on disk holding the markdown knowledge base. The `FileSystemVault` adapter is rooted at config `Vault:Root`.

### Configuration
Bound from `appsettings.json` (+ gitignored `appsettings.Development.json` for the DB connection string with its password): `Ollama:Url`, `Ollama:Model` (default `gemma4:e4b`), `Vault:Root`, and `ConnectionStrings:WhatsApp`. No secrets in source.

## Architecture

This codebase implements the pipeline described in `docs/DESIGN.md` — read that first. It is a **privacy-first personal knowledge base** that ingests WhatsApp/email messages, uses a local LLM to extract tasks/events/commitments, and stores everything as markdown files with YAML frontmatter (the "Open Knowledge Format"). **Files are the database**; there is no relational store for the KB itself — the graph lives in wikilinks between markdown files.

The architecture is **ports & adapters**: all logic lives in `Nameless.TaskList.Core` behind three interfaces in `Ports.fs` (`IMessageSource`, `IVault`, `IChatClient`); adapters implement them; the ASP.NET host composes them. The orchestration is a **deterministic chain** (DESIGN §6.1), but each LLM step runs through a **tool-enabled agent loop** — the model calls read-only tools to pull only the vault context it needs. **All file writes happen in `Pipeline.fs`; the model only ever calls read-only tools.**

Core modules, in compile order:

1. **`Library.fs`** — SQL queries (`Queries.GetMessageByIdAndChatJid`, `GetPreviousMessagesByChatIdAndJid`) + the `ChatMessage` record; sender/chat names normalised via `COALESCE` over the contacts table.
2. **`KnowledgeBase.fs`** — the markdown codec. `MarkdownFile.FromString`/`ToString` split/join YAML frontmatter + body (Markdig `UseYamlFrontMatter`); `Frontmatter.serialize`/`deserialize` use YamlDotNet with `UnderscoredNamingConvention` (`priority_weight` → `PriorityWeight`); `[<CLIMutable>]` records (`Context`, `Channel`, `Message`, `Topic`, `Task`) are the targets — add new DESIGN §4 concept types here; `Naming` holds slug + file-path rules (DESIGN §8).
3. **`Conversation.fs`** — Ollama wire types. The message DU (`System`/`User`/`Assistant`/`Tool`) serializes to Ollama's shape; `.Role`/`.Type` are **computed members** (not fields) so they serialize as constants, and `[<JsonPropertyName>]` maps `tool_calls`/`tool_name`/`index`. `Response.parseResponse` parses the reply (note: it lives in the nested `Response` module).
4. **`Ports.fs`** — the three interfaces. `IChatClient.Chat` is one round-trip; the loop lives in `Agent.fs`.
5. **`Tools.fs`** — read-only tools over `IVault` (`get_contexts`/`get_topics`/`get_topic`/`get_people`), each a `ToolDefinition` paired with a handler.
6. **`Agent.fs`** — `runConversation`: the tool-calling loop (dispatch tool calls → append results → repeat; 5-iteration guard raising `AgentError`).
7. **`Prompts.fs`** — DESIGN §7 system prompts + `Classification`/`TopicMatch` records and their parsers (System.Text.Json, snake_case, fence-tolerant, `Result`-returning — never throw on bad model output).
8. **`Pipeline.fs`** — `processMessage`: fetch → idempotency check (message-file existence, before any LLM call) → classify → topic match/create → task create → write Message → best-effort Topic update. Returns `PipelineResult` (`NotFound`/`Skipped`/`ProcessedNoise`/`Processed`/`LlmError`).
9. **`Adapters.fs`** — `PostgresMessageSource` (Npgsql), `FileSystemVault` (filesystem, never deletes), `OllamaChatClient` (HTTP, synchronous by design).

The `Nameless.TaskList` web project composes the adapters in DI and maps `POST /messages/process` → `Pipeline.processMessage` → an HTTP status (`ProcessMessage.fs` `toHttp`: 200/404/502, 500 on unexpected). **Out of scope this increment** (see `docs/superpowers/specs/2026-06-22-per-message-pipeline-design.md`): Event/Commitment/Person-stub writers, `Channel.last_processed`, index regeneration, digests.

## Conventions

- The KB markdown conventions (frontmatter schemas, status/priority enums, file-naming slugs, ISO-8601 dates with `+02:00`, never-delete/append-only rules) are authoritative in `docs/DESIGN.md` §4 and §8. Match them when generating or parsing vault files.
- When deserializing new frontmatter fields, mirror the snake_case YAML key as a PascalCase record field and rely on `UnderscoredNamingConvention` rather than adding explicit attributes.
- JSON sent to Ollama uses `JsonSerializerOptions.Web` (camelCase). Keep message-shape fields as records and protocol constants (`role`, `type`) as computed members.
