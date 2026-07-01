# Nameless.TaskList

A **privacy-first personal knowledge base**. It ingests messages (WhatsApp, email),
uses a **local** LLM to extract tasks, events, commitments, and relationships, and
stores everything as plain **markdown files with YAML frontmatter** — the *Open
Knowledge Format* (OKF). All processing happens on your own machine; no message
content leaves the local network.

The guiding principle is **files are the database**. There is no relational store for
the knowledge base itself: every concept (person, topic, task, event, message…) is a
markdown file, and the graph lives in the wikilinks between those files. The vault is
designed to be opened and edited directly in [Obsidian](https://obsidian.md).

See [`docs/DESIGN.md`](docs/DESIGN.md) for the full design — read that first if you
want the *why*.

## How it works

The system is a **ports & adapters** application written in F# on .NET 10.

- All logic lives in `Nameless.TaskList.Core`, behind three interfaces in `Ports.fs`:
  `IMessageSource` (where messages come from), `IVault` (the markdown store), and
  `IChatClient` (the local LLM).
- Adapters implement those ports (Postgres, filesystem, Ollama); the ASP.NET host in
  `Nameless.TaskList` composes them.
- The per-message pipeline (`Pipeline.fs`) is a **deterministic chain**: fetch →
  idempotency check → classify → topic match/create → task create → write. Each LLM
  step runs a **tool-enabled agent loop** — the model calls *read-only* tools to pull
  just the vault context it needs. **All file writes happen in the pipeline; the model
  only ever reads.**

The pipeline is **idempotent per message**: each message maps to one file, so
processing the same message twice is a no-op.

## Requirements

For the tests you need nothing but the .NET SDK — they run against in-memory fakes.
To actually run the pipeline against your own data you need:

- **.NET 10 SDK**
- **[Ollama](https://ollama.com)** at `http://localhost:11434`, with the models named
  in `appsettings.json` pulled (a chat model, an embedding model, and — for images — a
  vision model).
- **PostgreSQL** with a `whatsapp` database populated by a
  [whatsmeow](https://github.com/tulir/whatsmeow)-based WhatsApp bridge (tables
  `messages`, `chats`, `whatsmeow_contacts`). Only needed for the WhatsApp source.
- **An Obsidian vault** (any directory of markdown) to act as the knowledge base,
  pointed at by `Vault:Root`.
- Optional: an IMAP mailbox (email ingestion) and `whisper` on `PATH` (voice notes).

## Getting started

```bash
# Build everything
dotnet build

# Run the unit + endpoint tests (in-memory fakes; no live services needed)
dotnet test

# Run the ASP.NET host — exposes POST /messages/process { id, chatJid }
dotnet run --project src/Nameless.TaskList
```

The opt-in live-service integration suite (real Postgres/Ollama/Whisper) is excluded
from the default `dotnet test`; each test skips when its service is absent:

```bash
dotnet test tests/Nameless.TaskList.IntegrationTests -p:Integration=true
```

## Configuration

Configuration is bound from `appsettings.json`. Secrets are **not** in source — put
the database connection string (and any other credentials) in a gitignored
`appsettings.Development.json` or environment variables. Key settings:

| Setting | Purpose |
| --- | --- |
| `Vault:Root` | Path to the Obsidian vault (the knowledge base). |
| `Vault:UtcOffsetHours` | Timezone offset used when stamping vault files. |
| `Ollama:Url` / `Ollama:Model` / `Ollama:EmbedModel` / `Ollama:VisionModel` | Local LLM endpoint and models. |
| `ConnectionStrings:WhatsApp` | Postgres connection string (keep in `appsettings.Development.json`). |
| `Imap:*` | Email ingestion (disabled by default). |
| `WhatsApp:Listen:*` | Live WhatsApp notify-listener (disabled by default). |
| `Scheduler:*` | In-app scheduler for digests / reindex / refile (disabled by default). |

## Layout

```
src/Nameless.TaskList.Core   Domain logic behind the three ports (codec, agent loop, pipeline, adapters)
src/Nameless.TaskList        ASP.NET host — DI composition and HTTP endpoints
eval/Nameless.TaskList.Eval  Prompt-eval CLI: scores LLM steps against a hand-labelled gold dataset
eval/dataset                 Anonymised, synthetic gold cases + world fixtures (no real PII)
tests/…Core.Tests            xUnit unit tests over in-memory fakes
tests/…Tests                 Endpoint result → HTTP status mapping tests
tests/…IntegrationTests      Opt-in tests against real Postgres/Ollama/Whisper
docs/DESIGN.md               Authoritative design + KB markdown conventions
```

## Evaluating prompt quality

The extraction steps are scored against a hand-labelled gold dataset so prompt or
model changes can be measured rather than guessed:

```bash
dotnet run --project eval/Nameless.TaskList.Eval -- --model <model> --report out.md
```

The dataset under `eval/dataset` is entirely synthetic — the people, medical numbers,
and topics are fabricated fixtures, not real data.

## License

Released under the [MIT License](LICENSE).
