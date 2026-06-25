# Relationship Graph Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add typed person-to-person relationship edges (one markdown file per edge) captured inline by the pipeline, reconciled by `/reindex`, and queryable via an LLM tool, HTTP endpoints, and a browsable index.

**Architecture:** Ports & adapters F#/.NET 10 solution. A new `Relationship` concept type joins the existing markdown-as-database vault. Edges are written by a new best-effort LLM step in `Pipeline.processMessage` (fired only when ≥2 mentioned people resolve to existing person files), keyed idempotently on a canonical alphabetical filename. A pure `Relationships` module holds the edge-building and reconcile decisions; `Indexer` gains a deterministic, LLM-free render/reconcile pass; `Tools` and the web host expose query surfaces.

**Tech Stack:** F# / .NET 10, xUnit 2.9.2 + Xunit.SkippableFact, YamlDotNet (frontmatter), System.Text.Json (model output + web), ASP.NET minimal APIs, in-memory `FakeVault`/`FakeChatClient` for tests.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-06-25-relationship-graph-design.md`.
- **Never `private` on serialized record types** — a `private` record serializes to `{}` under both YamlDotNet and System.Text.Json (see the `fsharp-private-record-serialization` lesson). All frontmatter/DTO records are public `[<CLIMutable>]`.
- **Frontmatter convention:** snake_case YAML keys ↔ PascalCase record fields via `UnderscoredNamingConvention`; no explicit attributes.
- **Model-output parsers** use the existing `tryParse<'T>` discipline in `Prompts.fs`: System.Text.Json, `JsonNamingPolicy.SnakeCaseLower`, fence/chatter tolerant via `extractJson`, return `Result` — never throw on bad model output.
- **Vault is append-only / never deletes.** Reconciliation overwrites an edge file in place; it never deletes person or message files.
- **The inline pipeline step is best-effort:** wrapped so any failure leaves the rest of `processMessage` behaviour unchanged (mirrors the existing best-effort topic-update step).
- **Relation enum (the only valid `relation` values):** `parent-child`, `sibling`, `partner`, `patient-doctor`, `student-teacher`, `colleague`, `friend`, `other`.
- **Confidence values:** `high` (rank 2), `medium` (rank 1), `low` (rank 0). Inline writes require rank ≥ 1 (high or medium).
- **Index filename convention:** indexes are written as `{root}/index.md` (matches every other index; the spec's `_index.md` is superseded by this existing convention). `loadAll` already skips files ending `index.md`.
- **Core compile order** (`Nameless.TaskList.Core.fsproj`): `KnowledgeBase` → `Conversation` → `Ports` → `Tools` → `Agent` → `Prompts` → `Similarity` → `Pipeline` → `BulkProcessor` → `Indexer` → `Adapters` → `Weights` → `Digest`. The new `Relationships.fs` is inserted **after `Prompts.fs`, before `Similarity.fs`** (it depends on `KnowledgeBase` and `Prompts`).
- **Build/test:** `dotnet build` must pass; `dotnet test` (default, no live services) must pass. Run both before declaring the feature done.

---

### Task 1: `Relationship` record + canonical path naming

**Files:**
- Modify: `src/Nameless.TaskList.Core/KnowledgeBase.fs` (add record after the `Person` record ~line 167; add `relationshipPath` in the `Naming` module near `personPath` ~line 232)
- Test: `tests/Nameless.TaskList.Core.Tests/NamingTests.fs`

**Interfaces:**
- Produces: `KnowledgeBase.Relationship` record with fields `Type, Title, From, To, Relation, Descriptor, Confidence, People (string array), Source` (all public, `[<CLIMutable>]`).
- Produces: `Naming.relationshipPath : slugA:string -> slugB:string -> string` returning `relationships/{a}-{b}.md` where `a,b` are the two slugs in ordinal-ascending order (canonical, order-independent).

- [ ] **Step 1: Write the failing test**

Add to `tests/Nameless.TaskList.Core.Tests/NamingTests.fs`:

```fsharp
[<Fact>]
let ``relationshipPath orders slugs alphabetically`` () =
    Assert.Equal("relationships/dr-naidoo-ethan.md", Naming.relationshipPath "dr-naidoo" "ethan")

[<Fact>]
let ``relationshipPath is order-independent`` () =
    Assert.Equal(Naming.relationshipPath "ethan" "dr-naidoo", Naming.relationshipPath "dr-naidoo" "ethan")
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~NamingTests"`
Expected: FAIL — `relationshipPath` is not defined (compile error).

- [ ] **Step 3: Add the record**

In `src/Nameless.TaskList.Core/KnowledgeBase.fs`, immediately after the `Person` record (the block ending `Aliases: string array }`), add:

```fsharp
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
```

- [ ] **Step 4: Add the naming helper**

In `src/Nameless.TaskList.Core/KnowledgeBase.fs`, in the `Naming` module just after `personPath`, add:

```fsharp
    let relationshipPath (slugA: string) (slugB: string) : string =
        let a, b =
            if System.String.CompareOrdinal(slugA, slugB) <= 0 then slugA, slugB else slugB, slugA
        sprintf "relationships/%s-%s.md" a b
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~NamingTests"`
Expected: PASS (all NamingTests, including the two new ones).

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/KnowledgeBase.fs tests/Nameless.TaskList.Core.Tests/NamingTests.fs
git commit -m "feat: Relationship record + canonical relationshipPath"
```

---

### Task 2: Relationship-extraction prompt, DTO, and parser

**Files:**
- Modify: `src/Nameless.TaskList.Core/Prompts.fs` (add DTOs near the other `[<CLIMutable>]` records ~line 33; add prompt string near the other `*System` strings; add `parseRelationships` next to `parseTopicMatch` ~line 241)
- Test: `tests/Nameless.TaskList.Core.Tests/ParsingTests.fs`

**Interfaces:**
- Produces: `Prompts.RelationshipEdge` record — `From, To, Relation, Descriptor, Confidence` (all `string`, public `[<CLIMutable>]`).
- Produces: `Prompts.RelationshipExtraction` record — `Relationships : RelationshipEdge array` (public `[<CLIMutable>]`).
- Produces: `Prompts.relationshipExtractSystem : string` (system prompt).
- Produces: `Prompts.parseRelationships : raw:string -> Result<RelationshipExtraction, string>`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Nameless.TaskList.Core.Tests/ParsingTests.fs`:

```fsharp
[<Fact>]
let ``parseRelationships reads edges from snake_case json`` () =
    let raw = """{ "relationships": [ { "from": "ethan", "to": "dr-naidoo", "relation": "patient-doctor", "descriptor": "paediatrician since 2022", "confidence": "high" } ] }"""
    match Prompts.parseRelationships raw with
    | Ok x ->
        Assert.Equal(1, x.Relationships.Length)
        Assert.Equal("ethan", x.Relationships.[0].From)
        Assert.Equal("dr-naidoo", x.Relationships.[0].To)
        Assert.Equal("patient-doctor", x.Relationships.[0].Relation)
        Assert.Equal("high", x.Relationships.[0].Confidence)
    | Error e -> failwith e

[<Fact>]
let ``parseRelationships tolerates code fences`` () =
    let raw = "```json\n{ \"relationships\": [] }\n```"
    match Prompts.parseRelationships raw with
    | Ok x -> Assert.Equal(0, x.Relationships.Length)
    | Error e -> failwith e

[<Fact>]
let ``parseRelationships returns Error on garbage`` () =
    match Prompts.parseRelationships "not json at all" with
    | Ok _ -> failwith "expected Error"
    | Error _ -> ()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~ParsingTests"`
Expected: FAIL — `parseRelationships` not defined (compile error).

- [ ] **Step 3: Add the DTOs**

In `src/Nameless.TaskList.Core/Prompts.fs`, after the `TopicMatch` record (the block ending `NewTopicTitle: string }`), add:

```fsharp
    [<CLIMutable>]
    type RelationshipEdge =
        { From: string
          To: string
          Relation: string
          Descriptor: string
          Confidence: string }

    [<CLIMutable>]
    type RelationshipExtraction =
        { Relationships: RelationshipEdge array }
```

- [ ] **Step 4: Add the prompt**

In `src/Nameless.TaskList.Core/Prompts.fs`, after the `personStubSystem` string, add:

```fsharp
    let relationshipExtractSystem = """You identify explicit relationships between people for a personal knowledge base.

You are given the slugs of people already known to the knowledge base and the message that mentioned them.
Identify only relationships that are explicitly stated or strongly implied between two of those people.

Use the person slugs EXACTLY as given for "from" and "to".

"relation" MUST be one of:
  parent-child       (from = parent, to = child)
  patient-doctor     (from = patient, to = doctor)
  student-teacher    (from = student, to = teacher)
  sibling            (symmetric)
  partner            (symmetric)
  colleague          (symmetric)
  friend             (symmetric)
  other              (use only when none of the above fit)

"descriptor" is a short free-text detail (e.g. "paediatrician since 2022") or null.
"confidence" is one of: high, medium, low.

Respond ONLY with a JSON object in this exact format:

{
  "relationships": [
    { "from": "slug-a", "to": "slug-b", "relation": "patient-doctor", "descriptor": "string or null", "confidence": "high" }
  ]
}

If there are no clear relationships, respond with {"relationships": []}. No explanation."""
```

- [ ] **Step 5: Add the parser**

In `src/Nameless.TaskList.Core/Prompts.fs`, immediately after `let parseTopicMatch ... = tryParse<TopicMatch> raw`, add:

```fsharp
    let parseRelationships (raw: string) : Result<RelationshipExtraction, string> = tryParse<RelationshipExtraction> raw
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~ParsingTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList.Core/Prompts.fs tests/Nameless.TaskList.Core.Tests/ParsingTests.fs
git commit -m "feat: relationship extraction prompt, DTO, and parser"
```

---

### Task 3: Pure `Relationships` module (build + reconcile)

**Files:**
- Create: `src/Nameless.TaskList.Core/Relationships.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (add `<Compile Include="Relationships.fs" />` between `Prompts.fs` and `Similarity.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/RelationshipsTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj` (add `<Compile Include="RelationshipsTests.fs" />` after `ParsingTests.fs`)

**Interfaces:**
- Consumes: `KnowledgeBase.Relationship`, `KnowledgeBase.Naming.slug`, `Prompts.RelationshipEdge`.
- Produces: `Relationships.confidenceRank : string -> int` (`high`→2, `medium`→1, `low`→0, else -1).
- Produces: `Relationships.buildEdge : resolve:(string -> string option) -> source:string -> edge:Prompts.RelationshipEdge -> Relationship option`. Returns `None` when either endpoint slug is empty, the endpoints are equal, the relation is not in the enum, or either endpoint fails to resolve to a person path. `People` is `[| fromSlug; toSlug |]` (the resolved slugs, unsorted — the canonical filename sort happens in `Naming.relationshipPath`).
- Produces: `Relationships.reconcile : existing:Relationship option -> incoming:Relationship -> Relationship option`. `None` existing → `Some incoming`; else `Some incoming` only when its confidence rank is strictly greater than the existing edge's, otherwise `None`.

- [ ] **Step 1: Write the failing test**

Create `tests/Nameless.TaskList.Core.Tests/RelationshipsTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.RelationshipsTests

open Nameless.TaskList.Core
open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Prompts
open Xunit

let private resolve =
    function
    | "ethan" -> Some "people/family/ethan.md"
    | "dr-naidoo" -> Some "people/medical/dr-naidoo.md"
    | _ -> None

let private edge from' to' rel conf : RelationshipEdge =
    { From = from'; To = to'; Relation = rel; Descriptor = ""; Confidence = conf }

[<Fact>]
let ``buildEdge resolves endpoints and sets fields`` () =
    match Relationships.buildEdge resolve "messages/x.md" (edge "ethan" "dr-naidoo" "patient-doctor" "high") with
    | Some r ->
        Assert.Equal("Relationship", r.Type)
        Assert.Equal("people/family/ethan.md", r.From)
        Assert.Equal("people/medical/dr-naidoo.md", r.To)
        Assert.Equal("patient-doctor", r.Relation)
        Assert.Equal<string[]>([| "ethan"; "dr-naidoo" |], r.People)
        Assert.Equal("messages/x.md", r.Source)
    | None -> failwith "expected Some"

[<Fact>]
let ``buildEdge rejects unknown endpoint`` () =
    Assert.True((Relationships.buildEdge resolve "m" (edge "ethan" "stranger" "friend" "high")).IsNone)

[<Fact>]
let ``buildEdge rejects relation not in enum`` () =
    Assert.True((Relationships.buildEdge resolve "m" (edge "ethan" "dr-naidoo" "nemesis" "high")).IsNone)

[<Fact>]
let ``buildEdge rejects self edge`` () =
    Assert.True((Relationships.buildEdge resolve "m" (edge "ethan" "ethan" "sibling" "high")).IsNone)

[<Fact>]
let ``reconcile writes when none exists`` () =
    let inc = (Relationships.buildEdge resolve "m" (edge "ethan" "dr-naidoo" "patient-doctor" "medium")).Value
    Assert.True((Relationships.reconcile None inc).IsSome)

[<Fact>]
let ``reconcile upgrades on higher confidence only`` () =
    let lower = (Relationships.buildEdge resolve "m" (edge "ethan" "dr-naidoo" "patient-doctor" "medium")).Value
    let higher = (Relationships.buildEdge resolve "m" (edge "ethan" "dr-naidoo" "patient-doctor" "high")).Value
    Assert.True((Relationships.reconcile (Some lower) higher).IsSome)   // medium -> high upgrades
    Assert.True((Relationships.reconcile (Some higher) lower).IsNone)   // high -> medium skips
```

- [ ] **Step 2: Register the new test file**

In `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`, add after the `ParsingTests.fs` line:

```xml
        <Compile Include="RelationshipsTests.fs" />
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~RelationshipsTests"`
Expected: FAIL — `Relationships` module not defined (compile error).

- [ ] **Step 4: Create the module**

Create `src/Nameless.TaskList.Core/Relationships.fs`:

```fsharp
namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Prompts

/// Pure helpers for building and reconciling person-to-person relationship edges.
module Relationships =

    let private validRelations =
        set [ "parent-child"; "sibling"; "partner"; "patient-doctor"
              "student-teacher"; "colleague"; "friend"; "other" ]

    let confidenceRank (c: string) : int =
        match (if isNull c then "" else c).ToLowerInvariant().Trim() with
        | "high" -> 2
        | "medium" -> 1
        | "low" -> 0
        | _ -> -1

    /// Build a Relationship from an extracted edge. `resolve` maps a person slug to its
    /// file path (None = unknown person). Returns None when the edge is unusable.
    let buildEdge (resolve: string -> string option) (source: string) (edge: RelationshipEdge) : Relationship option =
        let fromSlug = Naming.slug edge.From
        let toSlug = Naming.slug edge.To
        let rel = (if isNull edge.Relation then "" else edge.Relation).ToLowerInvariant().Trim()
        if System.String.IsNullOrWhiteSpace fromSlug
           || System.String.IsNullOrWhiteSpace toSlug
           || fromSlug = toSlug then None
        elif not (Set.contains rel validRelations) then None
        else
            match resolve fromSlug, resolve toSlug with
            | Some fromPath, Some toPath ->
                let conf =
                    let c = (if isNull edge.Confidence then "" else edge.Confidence).ToLowerInvariant().Trim()
                    if confidenceRank c >= 0 then c else "low"
                Some { Type = "Relationship"
                       Title = sprintf "%s ↔ %s" fromSlug toSlug
                       From = fromPath
                       To = toPath
                       Relation = rel
                       Descriptor = (if isNull edge.Descriptor then "" else edge.Descriptor)
                       Confidence = conf
                       People = [| fromSlug; toSlug |]
                       Source = source }
            | _ -> None

    /// Decide whether to write `incoming` given any `existing` edge at the canonical path.
    let reconcile (existing: Relationship option) (incoming: Relationship) : Relationship option =
        match existing with
        | None -> Some incoming
        | Some ex ->
            if confidenceRank incoming.Confidence > confidenceRank ex.Confidence then Some incoming
            else None
```

- [ ] **Step 5: Register the module in the Core project**

In `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj`, add between the `Prompts.fs` and `Similarity.fs` lines:

```xml
        <Compile Include="Relationships.fs" />
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~RelationshipsTests"`
Expected: PASS (7 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList.Core/Relationships.fs src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj tests/Nameless.TaskList.Core.Tests/RelationshipsTests.fs tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj
git commit -m "feat: pure Relationships module (buildEdge + reconcile)"
```

---

### Task 4: `get_relationships` read-only tool

**Files:**
- Modify: `src/Nameless.TaskList.Core/Tools.fs` (add `open Nameless.TaskList.Core.KnowledgeBase`; add `getRelationships` after `getPeople` ~line 58)
- Test: `tests/Nameless.TaskList.Core.Tests/ToolsTests.fs`

**Interfaces:**
- Consumes: `IVault`, `KnowledgeBase.Relationship`, `KnowledgeBase.Naming.slug`, `KnowledgeBase.MarkdownFile`, `KnowledgeBase.Frontmatter`.
- Produces: `Tools.getRelationships : IVault -> Tool` — tool name `get_relationships`, one required string param `slug`; handler returns the concatenated edge files where the person (matched by `People` slug, normalised through `Naming.slug`) is an endpoint, or a "No relationships found" message.

- [ ] **Step 1: Write the failing test**

Add to `tests/Nameless.TaskList.Core.Tests/ToolsTests.fs` (uses `FakeVault` from `Fakes`, already opened there):

```fsharp
[<Fact>]
let ``get_relationships returns edges for a person`` () =
    let v = FakeVault()
    v.Seed("relationships/dr-naidoo-ethan.md",
           "---\ntype: Relationship\ntitle: Ethan ↔ Dr Naidoo\nfrom: people/family/ethan.md\nto: people/medical/dr-naidoo.md\nrelation: patient-doctor\ndescriptor: ''\nconfidence: high\npeople:\n  - ethan\n  - dr-naidoo\nsource: messages/x.md\n---\nbody")
    let tool = Tools.getRelationships (v :> IVault)
    let args = System.Collections.Generic.Dictionary<string, obj>()
    args.["slug"] <- box "ethan"
    let out = tool.Handler args
    Assert.Contains("relationships/dr-naidoo-ethan.md", out)
    Assert.Contains("patient-doctor", out)

[<Fact>]
let ``get_relationships reports none for unknown person`` () =
    let v = FakeVault()
    let tool = Tools.getRelationships (v :> IVault)
    let args = System.Collections.Generic.Dictionary<string, obj>()
    args.["slug"] <- box "nobody"
    Assert.Contains("No relationships found", tool.Handler args)
```

> Note: check the top of `ToolsTests.fs` for the existing `open` lines; it already opens `Nameless.TaskList.Core`, `Nameless.TaskList.Core.Ports`, and `Nameless.TaskList.Core.Tests.Fakes`. Add `open Nameless.TaskList.Core.KnowledgeBase` only if `IVault`/`FakeVault` references don't already resolve (they should from the existing opens).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~ToolsTests"`
Expected: FAIL — `getRelationships` not defined (compile error).

- [ ] **Step 3: Add the tool**

In `src/Nameless.TaskList.Core/Tools.fs`, add to the `open` block at the top:

```fsharp
open Nameless.TaskList.Core.KnowledgeBase
```

Then add after `getPeople` (after the line `(fun _ -> dumpDir vault "people")`):

```fsharp
    let getRelationships (vault: IVault) : Tool =
        define "get_relationships"
            "List the known relationships for a person, given their slug (e.g. dr-naidoo)."
            (oneStringParam "slug" "The person slug, e.g. dr-naidoo")
            (fun args ->
                let slug = Naming.slug (string args.["slug"])
                let matches =
                    vault.ListFilesRecursive "relationships"
                    |> List.filter (fun p -> not (p.EndsWith("index.md")))
                    |> List.filter (fun p ->
                        try
                            match (MarkdownFile.FromString(vault.Read p)).FrontMatter with
                            | Some fm ->
                                let r = Frontmatter.deserialize<Relationship> fm
                                (not (isNull r.People)) && (r.People |> Array.exists (fun s -> Naming.slug s = slug))
                            | None -> false
                        with _ -> false)
                if List.isEmpty matches then sprintf "No relationships found for '%s'." slug
                else matches |> List.map (fun p -> sprintf "## %s\n%s" p (vault.Read p)) |> String.concat "\n\n")
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~ToolsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Nameless.TaskList.Core/Tools.fs tests/Nameless.TaskList.Core.Tests/ToolsTests.fs
git commit -m "feat: get_relationships read-only tool"
```

---

### Task 5: Inline relationship extraction in the pipeline

**Files:**
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (insert a new step after the people-resolution loop — i.e. after the block ending at ~line 535 `MarkdownFile.ToString (Frontmatter.serialize finalRecord) body))`, and before `// --- Step: write the message record ...` ~line 537)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`
- Also wire `Tools.getRelationships` into the classify tool set so the agent can read relationships during classification.

**Interfaces:**
- Consumes: `Relationships.buildEdge`, `Relationships.confidenceRank`, `Relationships.reconcile`, `Prompts.parseRelationships`, `Prompts.relationshipExtractSystem`, `Naming.relationshipPath`, the existing `peopleIndex`/`resolvePerson` helpers in `Pipeline.fs`, and the existing `messagePath` value in scope at that point.
- Produces: edge files at `relationships/{a}-{b}.md` as a side effect; no change to `PipelineResult`.

- [ ] **Step 1: Write the failing test**

First inspect `PipelineTests.fs` to copy its existing harness (how it builds `PipelineDeps`, seeds people, and scripts `FakeChatClient` responses for the classify → topic → entities → person-stub sequence). The relationship step issues **one** additional `Chat` call, and only when ≥2 mentioned people already resolve to person files — so the scripted response queue needs one more entry at the end for tests that seed two people.

Add a focused test that seeds two existing people, scripts the full sequence ending with a relationship-extraction reply, and asserts an edge file is written:

```fsharp
[<Fact>]
let ``processMessage writes a relationship edge when two known people are co-mentioned`` () =
    // Arrange: seed two existing people so both mentions resolve (no stub LLM calls needed).
    // Build deps via the same helper the other PipelineTests use; seed:
    //   people/family/ethan.md     (title: Ethan)
    //   people/medical/dr-naidoo.md (title: Dr Naidoo)
    // Script FakeChatClient responses in pipeline order, the LAST being:
    //   Responses.final "{\"relationships\":[{\"from\":\"ethan\",\"to\":\"dr-naidoo\",\"relation\":\"patient-doctor\",\"descriptor\":\"paeds\",\"confidence\":\"high\"}]}"
    // Act: run processMessage.
    // Assert:
    Assert.True(vault.Files.ContainsKey("relationships/dr-naidoo-ethan.md"))
    Assert.Contains("relation: patient-doctor", vault.Files.["relationships/dr-naidoo-ethan.md"])
```

> Implementation note for the engineer: model this test on the nearest existing end-to-end `processMessage` test in `PipelineTests.fs` (same `PipelineDeps` construction, same classification JSON shape with `people_mentioned: ["Ethan","Dr Naidoo"]`, both flagged so they resolve to the seeded files). Because both people already exist, the person-stub step makes **no** LLM call for them; the only extra call versus the existing tests is the single relationship-extraction call, which must be the final scripted response. If the existing harness has a helper that supplies a default/looping chat response, prefer reusing it.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: FAIL — no `relationships/dr-naidoo-ethan.md` written (assertion fails).

- [ ] **Step 3: Add the inline step**

In `src/Nameless.TaskList.Core/Pipeline.fs`, immediately after the people-resolution loop (the `classification.PeopleMentioned |> Array.toList |> List.iter (...)` block ends with `MarkdownFile.ToString (Frontmatter.serialize finalRecord) body))`) and before the `// --- Step: write the message record ...` comment, insert:

```fsharp
            // --- Step: extract person-to-person relationships among resolved, co-mentioned people ---
            (try
                let relIndex = peopleIndex deps.Vault
                let resolve (s: string) = resolvePerson relIndex s
                let resolved =
                    classification.PeopleMentioned
                    |> Array.toList
                    |> List.choose (fun name ->
                        let s = Naming.slug name
                        match resolve s with Some _ -> Some s | None -> None)
                    |> List.distinct
                if List.length resolved >= 2 then
                    let user =
                        sprintf "People mentioned (use these exact slugs for from/to): %s\nMessage: %s"
                            (String.concat ", " resolved) msg.Content
                    match Prompts.parseRelationships
                              (Agent.runConversation deps.Chat [] Prompts.relationshipExtractSystem user) with
                    | Ok extraction when not (isNull extraction.Relationships) ->
                        for edge in extraction.Relationships do
                            match Relationships.buildEdge resolve messagePath edge with
                            | Some rel when Relationships.confidenceRank rel.Confidence >= 1 ->
                                let path = Naming.relationshipPath rel.People.[0] rel.People.[1]
                                let existing =
                                    if deps.Vault.Exists path then
                                        try
                                            (MarkdownFile.FromString(deps.Vault.Read path)).FrontMatter
                                            |> Option.map Frontmatter.deserialize<Relationship>
                                        with _ -> None
                                    else None
                                match Relationships.reconcile existing rel with
                                | Some toWrite ->
                                    let body =
                                        if System.String.IsNullOrWhiteSpace toWrite.Descriptor then toWrite.Title
                                        else toWrite.Descriptor
                                    deps.Vault.Write(path, MarkdownFile.ToString (Frontmatter.serialize toWrite) body)
                                | None -> ()
                            | _ -> ()
                    | _ -> ()
             with _ -> ())
```

- [ ] **Step 4: Wire the tool into the classify step**

In `src/Nameless.TaskList.Core/Pipeline.fs`, change the `classifyTools` binding (currently `let classifyTools = [ Tools.getContexts deps.Vault ]`) to:

```fsharp
            let classifyTools = [ Tools.getContexts deps.Vault; Tools.getPeople deps.Vault; Tools.getRelationships deps.Vault ]
```

> This exposes existing people and their relationships to the classifier as optional read-only context. It does not change scripted-test behaviour: `FakeChatClient` ignores the tools array and returns queued responses regardless.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS (the new test and all existing PipelineTests).

- [ ] **Step 6: Guard against regressions in the no/one-person path**

Confirm the existing PipelineTests (which seed zero or one person and do **not** script a trailing relationship response) still pass — the `>= 2 resolved` guard must short-circuit before any extra `Chat` call. If any existing test now dequeues an unexpected response, the guard or test seeding is wrong; fix the guard, not the test, unless the test genuinely co-mentions two resolvable people.

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "feat: inline relationship extraction in processMessage"
```

---

### Task 6: Reindex render + reconcile pass

**Files:**
- Modify: `src/Nameless.TaskList.Core/Indexer.fs` (add `Relationships` to `IndexSummary`; add `renderRelationships`; call it in `regenerate`)
- Test: `tests/Nameless.TaskList.Core.Tests/IndexerTests.fs`

**Interfaces:**
- Consumes: `KnowledgeBase.Relationship`, the existing private `loadAll`, `writeIndex`, `wikilink`, `nz` helpers in `Indexer`.
- Produces: `Indexer.IndexSummary` gains a `Relationships: int` field. `regenerate` writes `relationships/index.md` and includes the count; edges whose `From` or `To` person file no longer exists are dropped from both the index and the count.

- [ ] **Step 1: Write the failing test**

Add to `tests/Nameless.TaskList.Core.Tests/IndexerTests.fs`:

```fsharp
[<Fact>]
let ``regenerate writes a relationship index and drops dangling edges`` () =
    let v = FakeVault()
    v.Seed("people/family/ethan.md", "---\ntype: Person\ntitle: Ethan\n---\nbody")
    v.Seed("people/medical/dr-naidoo.md", "---\ntype: Person\ntitle: Dr Naidoo\n---\nbody")
    // live edge: both endpoints exist
    v.Seed("relationships/dr-naidoo-ethan.md",
           "---\ntype: Relationship\ntitle: Ethan ↔ Dr Naidoo\nfrom: people/family/ethan.md\nto: people/medical/dr-naidoo.md\nrelation: patient-doctor\ndescriptor: paeds\nconfidence: high\npeople:\n  - ethan\n  - dr-naidoo\nsource: messages/x.md\n---\nbody")
    // dangling edge: 'ghost' person file does not exist
    v.Seed("relationships/ethan-ghost.md",
           "---\ntype: Relationship\ntitle: Ethan ↔ Ghost\nfrom: people/family/ethan.md\nto: people/family/ghost.md\nrelation: friend\ndescriptor: ''\nconfidence: high\npeople:\n  - ethan\n  - ghost\nsource: messages/y.md\n---\nbody")
    let s = Indexer.regenerate (v :> IVault)
    Assert.Equal(1, s.Relationships)   // dangling edge dropped from count
    let idx = v.Files.["relationships/index.md"]
    Assert.Contains("type: Index", idx)
    Assert.Contains("title: Relationship Index", idx)
    Assert.Contains("[[relationships/dr-naidoo-ethan]]", idx)
    Assert.DoesNotContain("[[relationships/ethan-ghost]]", idx)
```

> Also update any existing IndexerTests that construct an expected `IndexSummary` by record literal or assert on all its fields — adding `Relationships` to the record means those must include `Relationships = 0`. Tests that read fields individually (e.g. `s.Tasks`) need no change.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~IndexerTests"`
Expected: FAIL — `IndexSummary` has no `Relationships` field (compile error).

- [ ] **Step 3: Extend `IndexSummary`**

In `src/Nameless.TaskList.Core/Indexer.fs`, change the `IndexSummary` record to add the field:

```fsharp
    type IndexSummary =
        { Tasks: int; Topics: int; Events: int; Commitments: int
          Notes: int; People: int; Channels: int; Relationships: int; Skipped: int }
```

- [ ] **Step 4: Add `renderRelationships`**

In `src/Nameless.TaskList.Core/Indexer.fs`, after `renderPeople` (before `regenerate`), add:

```fsharp
    let private renderRelationships (vault: IVault) : int * int =
        let items, skipped = loadAll<Relationship> vault "relationships"
        let exists (p: string) = not (System.String.IsNullOrWhiteSpace p) && vault.Exists p
        let live = items |> List.filter (fun (_, r) -> exists r.From && exists r.To)
        let sb = StringBuilder()
        for (path, r) in live |> List.sortBy fst do
            let desc = if System.String.IsNullOrWhiteSpace r.Descriptor then "" else sprintf " (%s)" r.Descriptor
            sb.AppendLine(sprintf "- %s — %s%s" (wikilink path) (nz r.Relation) desc) |> ignore
        writeIndex vault "relationships" "Relationship Index" (sb.ToString().TrimEnd())
        List.length live, skipped
```

- [ ] **Step 5: Call it in `regenerate`**

In `src/Nameless.TaskList.Core/Indexer.fs`, update `regenerate` to invoke the new pass and include its counts:

```fsharp
    let regenerate (vault: IVault) : IndexSummary =
        let tCount, tSkip = renderTasks vault
        let topCount, topSkip = renderTopics vault
        let evCount, evSkip = renderEvents vault
        let cmCount, cmSkip = renderCommitments vault
        let nCount, nSkip = renderNotes vault
        let pCount, pSkip = renderPeople vault
        let chCount, chSkip = renderChannels vault
        let relCount, relSkip = renderRelationships vault
        { Tasks = tCount; Topics = topCount; Events = evCount; Commitments = cmCount
          Notes = nCount; People = pCount; Channels = chCount; Relationships = relCount
          Skipped = tSkip + topSkip + evSkip + cmSkip + nSkip + pSkip + chSkip + relSkip }
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~IndexerTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList.Core/Indexer.fs tests/Nameless.TaskList.Core.Tests/IndexerTests.fs
git commit -m "feat: reindex relationship pass with dangling-edge drop"
```

---

### Task 7: HTTP query endpoints

**Files:**
- Modify: `src/Nameless.TaskList/ProcessMessage.fs` (add a `RelationshipsHandler` module with two `toHttp`-style helpers + the edge-loading logic)
- Modify: `src/Nameless.TaskList/Program.fs` (map `GET /relationships` and `GET /relationships/{slug}`)
- Test: `tests/Nameless.TaskList.Tests/EndpointTests.fs`

**Interfaces:**
- Consumes: `IVault`, `KnowledgeBase.Relationship`, `KnowledgeBase.MarkdownFile`, `KnowledgeBase.Frontmatter`, `KnowledgeBase.Naming.slug`.
- Produces: `RelationshipsHandler.loadEdges : IVault -> Relationship list` (all live-parsed edges, index.md excluded); `RelationshipsHandler.allToHttp : IVault -> IResult` (200, JSON array); `RelationshipsHandler.forPersonToHttp : IVault -> string -> IResult` (200, JSON array filtered by person slug — empty array, still 200, for an unknown person).

- [ ] **Step 1: Write the failing test**

Inspect `tests/Nameless.TaskList.Tests/EndpointTests.fs` for its `statusOfResult` helper (used by the reindex/digest/bulk tests). Add:

```fsharp
[<Fact>]
let ``relationships endpoints return 200`` () =
    let v = FakeVault()
    v.Seed("relationships/dr-naidoo-ethan.md",
           "---\ntype: Relationship\ntitle: Ethan ↔ Dr Naidoo\nfrom: people/family/ethan.md\nto: people/medical/dr-naidoo.md\nrelation: patient-doctor\ndescriptor: ''\nconfidence: high\npeople:\n  - ethan\n  - dr-naidoo\nsource: messages/x.md\n---\nbody")
    Assert.Equal(200, statusOfResult (RelationshipsHandler.allToHttp (v :> IVault)))
    Assert.Equal(200, statusOfResult (RelationshipsHandler.forPersonToHttp (v :> IVault) "ethan"))
    Assert.Equal(200, statusOfResult (RelationshipsHandler.forPersonToHttp (v :> IVault) "nobody"))
```

> `EndpointTests.fs` needs access to `FakeVault`. If it does not already reference the Core.Tests `Fakes`, define a tiny inline vault in the test file instead: a class implementing `IVault` over a `Dictionary<string,string>` with the same members as `Fakes.FakeVault` (Exists/Read/Write/ListFiles/ListFilesRecursive). Check the file's existing `open`s first; reuse whatever vault fake the project already exposes to this test assembly.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Tests`
Expected: FAIL — `RelationshipsHandler` not defined (compile error).

- [ ] **Step 3: Add the handler**

In `src/Nameless.TaskList/ProcessMessage.fs`, add (after the existing handler modules; add `open Nameless.TaskList.Core` and `open Nameless.TaskList.Core.KnowledgeBase` and `open Nameless.TaskList.Core.Ports` at the top of the file if not already present):

```fsharp
module RelationshipsHandler =
    open Nameless.TaskList.Core
    open Nameless.TaskList.Core.KnowledgeBase
    open Nameless.TaskList.Core.Ports

    /// All live, parseable relationship edges (index.md excluded).
    let loadEdges (vault: IVault) : Relationship list =
        vault.ListFilesRecursive "relationships"
        |> List.filter (fun p -> not (p.EndsWith("index.md")))
        |> List.choose (fun p ->
            try
                match (MarkdownFile.FromString(vault.Read p)).FrontMatter with
                | Some fm -> Some(Frontmatter.deserialize<Relationship> fm)
                | None -> None
            with _ -> None)

    let private hasPerson (slug: string) (r: Relationship) =
        (not (isNull r.People)) && (r.People |> Array.exists (fun s -> Naming.slug s = slug))

    let allToHttp (vault: IVault) : IResult =
        Results.Ok(box (loadEdges vault))

    let forPersonToHttp (vault: IVault) (slug: string) : IResult =
        let s = Naming.slug slug
        Results.Ok(box (loadEdges vault |> List.filter (hasPerson s)))
```

- [ ] **Step 4: Map the routes**

In `src/Nameless.TaskList/Program.fs`, after the `/reindex` mapping (~line 83), add:

```fsharp
        app.MapGet("/relationships", System.Func<IVault, Microsoft.AspNetCore.Http.IResult>(
            fun (vault: IVault) ->
                try RelationshipsHandler.allToHttp vault
                with ex -> Results.Json({| error = ex.Message |}, statusCode = 500))) |> ignore

        app.MapGet("/relationships/{slug}", System.Func<string, IVault, Microsoft.AspNetCore.Http.IResult>(
            fun (slug: string) (vault: IVault) ->
                try RelationshipsHandler.forPersonToHttp vault slug
                with ex -> Results.Json({| error = ex.Message |}, statusCode = 500))) |> ignore
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList/ProcessMessage.fs src/Nameless.TaskList/Program.fs tests/Nameless.TaskList.Tests/EndpointTests.fs
git commit -m "feat: GET /relationships and /relationships/{slug} endpoints"
```

---

### Task 8: Docs, full build/test, and roadmap update

**Files:**
- Modify: `docs/DESIGN.md` (§9 — mark the relationship-graph item delivered, first increment)
- Modify: `/home/kevin/.claude/projects/-home-kevin-development-Nameless-TaskList/memory/roadmap.md` (move relationship-graph from remaining backlog to the done log)

- [ ] **Step 1: Full build**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Full test run (default suite, no live services)**

Run: `dotnet test`
Expected: All tests pass (Core.Tests + Tests). No live Postgres/Ollama/Whisper needed.

- [ ] **Step 3: Update DESIGN §9**

In `docs/DESIGN.md`, update the `Relationship graph` bullet under §9 to note the first increment is delivered, e.g.:

```markdown
- **Relationship graph:** *(first increment delivered 2026-06-25)* A `relationships/` directory of one-file-per-edge person-to-person relationships (typed enum + free-form descriptor), captured inline by the pipeline, reconciled by `/reindex`, and queryable via the `get_relationships` tool and `GET /relationships[/{slug}]`. Remaining: multiple typed edges per pair, person↔org/location edges, relationship strength.
```

- [ ] **Step 4: Update the roadmap memory**

In `/home/kevin/.claude/projects/-home-kevin-development-Nameless-TaskList/memory/roadmap.md`, edit the §9 backlog line so `relationship graph` is no longer listed as not-yet-done, and add a done-log entry summarising the increment (record type, inline capture, reindex reconcile, three query surfaces, commit/merge ref once merged). Keep it to the file's existing terse style.

- [ ] **Step 5: Commit**

```bash
git add docs/DESIGN.md
git commit -m "docs: mark relationship graph first increment delivered"
```

> The roadmap memory file lives outside the repo; it is saved via the Write tool, not committed to git.

---

## Self-Review

**Spec coverage:**
- §1 concept type & storage → Task 1 (record + canonical path), enum validation in Task 3.
- §1 enum + free-form descriptor → Task 2 (prompt/DTO carries `descriptor`), Task 3 (enum gate).
- §1 direction (from/to) + one-edge-per-pair → Task 1 (canonical path = the per-pair key), Task 3 (`buildEdge`/`reconcile`).
- §2 inline capture (≥2 resolved people, best-effort) → Task 5.
- §2 backfill via bulk reprocess → no code (existing `/messages/process-since` re-runs `processMessage`); noted in spec, nothing to build.
- §2 reindex reconcile, LLM-free, dangling-ref drop → Task 6.
- §3 LLM tool → Task 4; wired into classify in Task 5.
- §3 HTTP endpoints → Task 7.
- §3 reindex view → Task 6.
- §4 testing → tests in every task (Naming, Parsing, Relationships, Tools, Pipeline, Indexer, Endpoint).
- §5 non-goals → none implemented (correct).

**Placeholder scan:** No TBD/TODO. The one prose-guided spot is Task 5 Step 1, which gives the engineer the exact assertions and the harness it must mirror (`PipelineTests.fs` is the source of truth for `PipelineDeps` construction, which varies and must not be guessed/duplicated wrongly). All code steps contain real code.

**Type consistency:** `Relationship` fields are identical everywhere (Task 1 record; used in Tasks 3/4/6/7). `RelationshipEdge`/`RelationshipExtraction` identical in Tasks 2/3/5. `confidenceRank`/`buildEdge`/`reconcile` signatures match between Task 3 definition and Task 5 use. `IndexSummary` gains exactly one field `Relationships`, consistently constructed in Task 6. `relationshipPath` argument order is irrelevant by design (order-independent) and always called with `rel.People.[0] rel.People.[1]`.

**Naming reconciliation:** Spec said `relationships/_index.md`; this plan uses `relationships/index.md` to match the existing index convention and `loadAll`'s `index.md` skip filter. Flagged in Global Constraints; spec to be updated to match.
