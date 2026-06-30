# Person Context Re-filing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A maintenance pass that re-files each person into the context their `Role` implies, moving the markdown file between `people/<context>/` folders via the vault relocate primitive.

**Architecture:** A new prompted step `Steps.classifyPersonContext` maps a person's role to one of the five `knownContexts`; a Core `Refiler` module (pure `decideRefile` + `run`) scans `people/**` and moves mismatches with `IVault.Relocate` (active→active, no `.trash/`); the host exposes it as `POST /people/refile` and a scheduler slot through the existing `MaintenanceTasks` runner.

**Tech Stack:** F# / .NET 10, xUnit 2.9.2.

## Global Constraints

- **Build must pass before any task is done:** `dotnet build` → 0 errors, 0 warnings.
- **Run tests per-project:** `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1` and `dotnet test tests/Nameless.TaskList.Tests -p:nodeReuse=false -maxcpucount:1`.
- **`knownContexts`** (already in `Steps`): exactly `family`, `medical`, `school`, `finance`, `professional`.
- **`classifyPersonContext` returns one of those or `None`** (off-list/garbage → `None`); best-effort, never throws into the pass.
- **Re-filing is active→active:** use `IVault.Relocate(old, new)` directly — NOT `Vault.retire`/`.trash/`.
- **`Relocate` gives no success signal** (its doc comment says so): after a move, confirm `Exists newPath && not (Exists oldPath)` before patching frontmatter; a `dst`-exists collision is a no-op, so the pass must `Exists`-check the target first and SKIP collisions (de-dup's job, not re-filing's).
- **Links are slug-based** (the slug/filename is unchanged by a re-file) — do NOT rewrite any links.
- F# compile order: a file may only reference types defined in files above it in `Nameless.TaskList.Core.fsproj`.

---

### Task 1: `Steps.classifyPersonContext` + `Prompts.personContextSystem`

A prompted step that classifies a person's role into one of `knownContexts`.

**Files:**
- Modify: `src/Nameless.TaskList.Core/Prompts.fs` (add `personContextSystem`, after `personStubSystem`)
- Modify: `src/Nameless.TaskList.Core/Steps.fs` (add `classifyPersonContext`, after `createPersonStub`)
- Test: `tests/Nameless.TaskList.Core.Tests/StepsTests.fs` (append)

**Interfaces:**
- Consumes: `Steps.knownContexts : string list`; `Agent.runConversation`; `Prompts.personContextSystem`.
- Produces: `Steps.classifyPersonContext : IChatClient -> name:string -> role:string -> aliases:string array -> string option`

- [ ] **Step 1: Write the failing tests**

Append to `tests/Nameless.TaskList.Core.Tests/StepsTests.fs`:

```fsharp
[<Fact>]
let ``classifyPersonContext returns the on-list context`` () =
    let chat = FakeChatClient([ Responses.final "medical" ])
    Assert.Equal(Some "medical", Steps.classifyPersonContext chat "Dr Naidoo" "doctor" [| "naidoo" |])

[<Fact>]
let ``classifyPersonContext tolerates surrounding punctuation and case`` () =
    let chat = FakeChatClient([ Responses.final "Medical." ])
    Assert.Equal(Some "medical", Steps.classifyPersonContext chat "Dr Naidoo" "doctor" [||])

[<Fact>]
let ``classifyPersonContext returns None for an off-list reply`` () =
    let chat = FakeChatClient([ Responses.final "spiritual" ])
    Assert.Equal(None, Steps.classifyPersonContext chat "Guru" "advisor" [||])
```

Note: `FakeChatClient` and `Responses.final` are already used throughout `StepsTests.fs` (from `Nameless.TaskList.Core.Tests.Fakes`); no new open needed.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~StepsTests.classifyPersonContext"`
Expected: FAIL — build error, `Steps.classifyPersonContext` / `Prompts.personContextSystem` not defined.

- [ ] **Step 3: Add the system prompt to `Prompts.fs`**

In `src/Nameless.TaskList.Core/Prompts.fs`, after the `personStubSystem` string, add:

```fsharp
    let personContextSystem = """You are filing a person into one life-context for a personal knowledge base.
Given a person's name, role, and aliases, reply with EXACTLY ONE of these context labels and nothing else:
family, medical, school, finance, professional.
Guidance: medical = doctors, nurses, dentists, therapists, medical-aid contacts; school = teachers, tutors, principals, coaches, classmates and their parents; finance = accountants, bankers, financial advisers, insurers; professional = colleagues, managers, clients, business contacts; family = relatives and personal or household contacts.
Reply with the single lowercase label only — no punctuation, no explanation."""
```

- [ ] **Step 4: Add `classifyPersonContext` to `Steps.fs`**

In `src/Nameless.TaskList.Core/Steps.fs`, after the `createPersonStub` function, add:

```fsharp
    /// Classify a person into one of `knownContexts` from their role (LLM, tool-free). Returns the
    /// chosen context, or None when the model replies off-list. Best-effort; never throws.
    let classifyPersonContext (chat: IChatClient) (name: string) (role: string) (aliases: string array) : string option =
        let aliasText = if isNull aliases then "" else String.concat ", " (Array.toList aliases)
        let user =
            sprintf "Name: %s\nRole: %s\nAliases: %s"
                (if isNull name then "" else name) (if isNull role then "" else role) aliasText
        let reply = Agent.runConversation chat [] Prompts.personContextSystem user
        // Keep only letters so trailing punctuation / casing ("Medical.") still matches.
        let letters =
            (if isNull reply then "" else reply)
            |> Seq.filter System.Char.IsLetter
            |> Seq.toArray
        let cleaned = System.String(letters).ToLowerInvariant()
        if List.contains cleaned knownContexts then Some cleaned else None
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~StepsTests.classifyPersonContext"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Prompts.fs src/Nameless.TaskList.Core/Steps.fs \
        tests/Nameless.TaskList.Core.Tests/StepsTests.fs
git commit -m "feat: Steps.classifyPersonContext classifies a person's role into a known context"
```

---

### Task 2: `Refiler` Core module

The maintenance logic: a pure `decideRefile` and a `run` that scans, classifies, moves, and patches frontmatter.

**Files:**
- Create: `src/Nameless.TaskList.Core/Refiler.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (register after `Indexer.fs`)
- Test: `tests/Nameless.TaskList.Core.Tests/RefilerTests.fs` (+ register in the test fsproj)

**Interfaces:**
- Consumes: `Steps.classifyPersonContext` (Task 1); `IVault` (`Relocate`/`Read`/`Write`/`Exists`/`ListFilesRecursive`); `Naming.personPath`; `MarkdownFile`/`Frontmatter`/`Person` (KnowledgeBase).
- Produces:
  - `Refiler.RefileAction` = `NoChange | Refile of target:string | SkipCollision | SkipUnknown`
  - `Refiler.decideRefile : currentContext:string -> target:string option -> targetExists:bool -> RefileAction`
  - `Refiler.RefileSummary = { Scanned:int; Refiled:int; Skipped:int }`
  - `Refiler.run : IVault -> IChatClient -> RefileSummary`

- [ ] **Step 1: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/RefilerTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.RefilerTests

open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

// --- decideRefile (pure) ---

[<Fact>]
let ``decideRefile NoChange when target equals current`` () =
    Assert.Equal(Refiler.NoChange, Refiler.decideRefile "medical" (Some "medical") false)

[<Fact>]
let ``decideRefile Refile when target differs and free`` () =
    Assert.Equal(Refiler.Refile "medical", Refiler.decideRefile "family" (Some "medical") false)

[<Fact>]
let ``decideRefile SkipCollision when target occupied`` () =
    Assert.Equal(Refiler.SkipCollision, Refiler.decideRefile "family" (Some "medical") true)

[<Fact>]
let ``decideRefile SkipUnknown when target is None`` () =
    Assert.Equal(Refiler.SkipUnknown, Refiler.decideRefile "family" None false)

// --- run over a FakeVault ---

let private personMd (role: string) (ctx: string) =
    // Triple-quoted so the YAML keeps real newlines (no \n-escaping ambiguity).
    sprintf """---
type: Person
title: Dr Naidoo
role: %s
context:
  - %s
channel: ''
phone: ''
email: ''
tags: []
aliases: []
---
Stub.""" role ctx

[<Fact>]
let ``run moves a misfiled doctor to medical and puts medical first in Context`` () =
    let vault = FakeVault()
    vault.Seed("people/family/dr-naidoo.md", personMd "doctor" "family")
    let chat = FakeChatClient([ Responses.final "medical" ])
    let summary = Refiler.run (vault :> IVault) chat
    Assert.False((vault :> IVault).Exists "people/family/dr-naidoo.md")
    Assert.True((vault :> IVault).Exists "people/medical/dr-naidoo.md")
    let moved = KnowledgeBase.MarkdownFile.FromString ((vault :> IVault).Read "people/medical/dr-naidoo.md")
    let p = KnowledgeBase.Frontmatter.deserialize<KnowledgeBase.Person> moved.FrontMatter.Value
    Assert.Equal("medical", p.Context.[0])
    Assert.Equal(1, summary.Refiled)
    Assert.Equal(0, summary.Skipped)

[<Fact>]
let ``run leaves an already-correct person untouched`` () =
    let vault = FakeVault()
    vault.Seed("people/medical/dr-naidoo.md", personMd "doctor" "medical")
    let chat = FakeChatClient([ Responses.final "medical" ])
    let summary = Refiler.run (vault :> IVault) chat
    Assert.True((vault :> IVault).Exists "people/medical/dr-naidoo.md")
    Assert.Equal(0, summary.Refiled)
    Assert.Equal(0, summary.Skipped)

[<Fact>]
let ``run skips when the target folder already holds the slug`` () =
    let vault = FakeVault()
    vault.Seed("people/family/dr-naidoo.md", personMd "doctor" "family")
    vault.Seed("people/medical/dr-naidoo.md", personMd "doctor" "medical")
    let chat = FakeChatClient([ Responses.final "medical"; Responses.final "medical" ])
    let summary = Refiler.run (vault :> IVault) chat
    Assert.True((vault :> IVault).Exists "people/family/dr-naidoo.md")
    Assert.True((vault :> IVault).Exists "people/medical/dr-naidoo.md")
    Assert.Equal(0, summary.Refiled)
    Assert.Equal(1, summary.Skipped)
```

Register it in `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`, after the `StepsTests.fs` line:

```xml
        <Compile Include="RefilerTests.fs" />
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~RefilerTests"`
Expected: FAIL — build error, `Refiler` is not defined.

- [ ] **Step 3: Create `Refiler.fs`**

```fsharp
namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Ports

/// Re-files people into the context their role implies (LLM-classified). A maintenance pass:
/// scans people/**, moves any whose folder disagrees with the classified context, and fixes the
/// Context frontmatter so the folder and Context[0] agree. Active->active via IVault.Relocate.
module Refiler =

    /// The routing decision for one person. Pure; the move/IO happens in `run`.
    type RefileAction =
        | NoChange
        | Refile of target: string
        | SkipCollision
        | SkipUnknown

    /// Decide what to do given the current context folder, the classified target (None = failed/
    /// off-list), and whether the target path is already occupied.
    let decideRefile (currentContext: string) (target: string option) (targetExists: bool) : RefileAction =
        match target with
        | None -> SkipUnknown
        | Some t when t = currentContext -> NoChange
        | Some _ when targetExists -> SkipCollision
        | Some t -> Refile t

    /// Counts for one pass. Scanned = people parsed; Refiled = moved; Skipped = collision/off-list/
    /// failed. Already-correct people are neither refiled nor skipped (Scanned - Refiled - Skipped).
    type RefileSummary = { Scanned: int; Refiled: int; Skipped: int }

    /// Re-file every person under people/**. Best-effort per person (any failure -> skipped).
    let run (vault: IVault) (chat: IChatClient) : RefileSummary =
        let mutable scanned = 0
        let mutable refiled = 0
        let mutable skipped = 0
        for path in vault.ListFilesRecursive "people" do
            try
                let segments = path.Split('/')
                // Expect people/<ctx>/<slug>.md (3 segments); anything else is not a filed person.
                if segments.Length = 3 && segments.[0] = "people" then
                    let mf = MarkdownFile.FromString (vault.Read path)
                    match mf.FrontMatter with
                    | Some fm ->
                        let p = Frontmatter.deserialize<Person> fm
                        scanned <- scanned + 1
                        let currentContext = segments.[1]
                        let slug = System.IO.Path.GetFileNameWithoutExtension path
                        let target = Steps.classifyPersonContext chat p.Title p.Role p.Aliases
                        let newPath = match target with | Some t -> Naming.personPath t slug | None -> ""
                        let targetExists = target.IsSome && vault.Exists newPath
                        match decideRefile currentContext target targetExists with
                        | Refile t ->
                            vault.Relocate(path, newPath)
                            // Relocate gives no success signal: confirm the move before patching.
                            if vault.Exists newPath && not (vault.Exists path) then
                                let moved = MarkdownFile.FromString (vault.Read newPath)
                                match moved.FrontMatter with
                                | Some mfm ->
                                    let mp = Frontmatter.deserialize<Person> mfm
                                    let others =
                                        if isNull mp.Context then [||]
                                        else mp.Context |> Array.filter (fun c -> c <> t)
                                    let updated = { mp with Context = Array.append [| t |] others }
                                    vault.Write(newPath, MarkdownFile.ToString (Frontmatter.serialize updated) moved.Content)
                                | None -> ()
                                refiled <- refiled + 1
                            else
                                skipped <- skipped + 1
                        | SkipCollision -> skipped <- skipped + 1
                        | SkipUnknown -> skipped <- skipped + 1
                        | NoChange -> ()
                    | None -> ()
            with _ ->
                skipped <- skipped + 1
        { Scanned = scanned; Refiled = refiled; Skipped = skipped }
```

Register it in `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` immediately **after** the `Indexer.fs` line:

```xml
        <Compile Include="Indexer.fs" />
        <Compile Include="Refiler.fs" />
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~RefilerTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Nameless.TaskList.Core/Refiler.fs src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj \
        tests/Nameless.TaskList.Core.Tests/RefilerTests.fs
git commit -m "feat: Refiler module re-files people into their role-implied context"
```

---

### Task 3: Host wiring (endpoint + scheduler + config)

Expose the pass as `POST /people/refile` and a scheduler slot through `MaintenanceTasks`.

**Files:**
- Modify: `src/Nameless.TaskList/Scheduler.fs` (`MaintenanceTasks.refilePeople`)
- Modify: `src/Nameless.TaskList/ProcessMessage.fs` (`RefileHandler.toHttp`)
- Modify: `src/Nameless.TaskList/Program.fs` (endpoint + scheduler task + `runTask` arm)
- Modify: `src/Nameless.TaskList/appsettings.json` (`Scheduler:RefilePeople`)
- Test: `tests/Nameless.TaskList.Tests/EndpointTests.fs` (append handler-mapping test)

**Interfaces:**
- Consumes: `Refiler.run` / `Refiler.RefileSummary` (Task 2); the existing `MaintenanceTasks` module, the scheduler `tasks` list + `runTask` dispatch, and the `ReindexHandler`-style handler pattern.
- Produces: `MaintenanceTasks.refilePeople : IConfiguration -> IVault -> IChatClient -> Refiler.RefileSummary`; `RefileHandler.toHttp : Refiler.RefileSummary -> IResult`.

- [ ] **Step 1: Write the failing endpoint-mapping test**

Append to `tests/Nameless.TaskList.Tests/EndpointTests.fs`:

```fsharp
[<Fact>]
let ``refile summary maps to 200`` () =
    let s : Nameless.TaskList.Core.Refiler.RefileSummary = { Scanned = 3; Refiled = 1; Skipped = 1 }
    Assert.Equal(200, statusOfResult (RefileHandler.toHttp s))
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~refile summary"`
Expected: FAIL — build error, `RefileHandler` not defined.

- [ ] **Step 3: Add `MaintenanceTasks.refilePeople`**

In `src/Nameless.TaskList/Scheduler.fs`, inside `module MaintenanceTasks`, after the `digest` function, add:

```fsharp
    let refilePeople (cfg: IConfiguration) (vault: IVault) (chat: IChatClient) : Refiler.RefileSummary =
        ignore cfg
        Refiler.run vault chat
```

- [ ] **Step 4: Add `RefileHandler.toHttp`**

In `src/Nameless.TaskList/ProcessMessage.fs`, after the `DigestHandler` module, add:

```fsharp
module RefileHandler =
    /// Maps a re-file summary to a 200 response.
    let toHttp (s: Nameless.TaskList.Core.Refiler.RefileSummary) : IResult =
        Results.Ok(box {| scanned = s.Scanned; refiled = s.Refiled; skipped = s.Skipped |})
```

- [ ] **Step 5: Add the endpoint in `Program.fs`**

In `src/Nameless.TaskList/Program.fs`, immediately after the `app.MapPost("/reindex", …)` block, add:

```fsharp
        app.MapPost("/people/refile", System.Func<IVault, IChatClient, Microsoft.AspNetCore.Http.IResult>(
            fun (vault: IVault) (chat: IChatClient) ->
                try MaintenanceTasks.refilePeople cfg vault chat |> RefileHandler.toHttp
                with ex -> Results.Json({| error = ex.Message |}, statusCode = 500))) |> ignore
```

- [ ] **Step 6: Wire the scheduler task**

In `src/Nameless.TaskList/Program.fs`, in the `if cfg.["Scheduler:Enabled"] = "true"` block, add the refile task to the `tasks` list (after the `"reindex"` entry):

```fsharp
                let tasks =
                    [ "daily-digest",  cfg.["Scheduler:DailyDigest"]
                      "weekly-digest", cfg.["Scheduler:WeeklyDigest"]
                      "reindex",       cfg.["Scheduler:Reindex"]
                      "refile-people", cfg.["Scheduler:RefilePeople"] ]
                    |> List.choose (fun (name, s) ->
                        Scheduler.parseSpec s |> Option.map (fun spec -> ({ Name = name; Spec = spec } : Scheduler.ScheduledTask)))
```

and add a dispatch arm to `runTask` (after the `"reindex"` arm, before the `| other ->` arm):

```fsharp
                        | "refile-people" -> MaintenanceTasks.refilePeople cfg vault chat |> ignore; logger.LogInformation("ran scheduled task {Name}", t.Name)
```

- [ ] **Step 7: Add the config slot**

In `src/Nameless.TaskList/appsettings.json`, add `"RefilePeople": ""` to the `Scheduler` object (a blank spec = disabled), e.g.:

```json
  "Scheduler": {
    "Enabled": false,
    "DailyDigest": "07:00",
    "WeeklyDigest": "Mon 07:00",
    "Reindex": "03:00",
    "RefilePeople": "",
    "CheckIntervalSeconds": 60
  }
```

- [ ] **Step 8: Build the whole solution**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 9: Run both suites (regression)**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`
Expected: all pass (338 prior + 10 new across Tasks 1–2 = 348).
Run: `dotnet test tests/Nameless.TaskList.Tests -p:nodeReuse=false -maxcpucount:1`
Expected: all pass (18 prior + 1 new = 19).

- [ ] **Step 10: Commit**

```bash
git add src/Nameless.TaskList/Scheduler.fs src/Nameless.TaskList/ProcessMessage.fs \
        src/Nameless.TaskList/Program.fs src/Nameless.TaskList/appsettings.json \
        tests/Nameless.TaskList.Tests/EndpointTests.fs
git commit -m "feat: wire POST /people/refile + scheduler slot through MaintenanceTasks"
```

---

## Self-Review

**Spec coverage:**
- `Steps.classifyPersonContext` (role → known context or None) → Task 1. ✓
- `Refiler.decideRefile` (pure routing) + `RefileSummary` + `run` (scan/classify/move/patch) → Task 2. ✓
- Active→active move via `IVault.Relocate`, no `.trash/`; no-signal confirm; collision skip; Context[0] fix; no link rewrite → Task 2 `run`. ✓
- `MaintenanceTasks.refilePeople`, `RefileHandler.toHttp`, `POST /people/refile`, `Scheduler:RefilePeople` slot → Task 3. ✓
- Tests: classify on/off-list, decideRefile cases, run move/already-correct/collision, handler→HTTP → Tasks 1–3. ✓

**Placeholder scan:** No TBD/TODO; every code step is complete.

**Type consistency:** `classifyPersonContext`'s signature is identical in Task 1 (def), Task 2 (`run` call site), and the tests. `Refiler.RefileSummary` field names (`Scanned`/`Refiled`/`Skipped`) match across `run`, the handler, and the tests. `RefileAction` cases (`NoChange`/`Refile`/`SkipCollision`/`SkipUnknown`) match `decideRefile`, `run`'s match, and the `decideRefile` tests. `Naming.personPath target slug` argument order matches its definition (`personPath context personSlug`). The scheduler `tasks`-list tuple shape and `runTask` arm match the existing `reindex` wiring exactly.

**Note on counts:** the "348 / 19" totals in Task 3 Step 9 are the expected sums (338+10, 18+1); if the exact number differs slightly, 0 failures is what matters.

## Out of Scope (per spec)

Retroactive de-dup (the collision case), editing a person's `Role`, index regeneration (`/reindex` owns it), non-person entities.
