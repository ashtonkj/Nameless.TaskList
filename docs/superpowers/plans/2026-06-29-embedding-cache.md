# Embedding Cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop re-embedding unchanged match-candidate documents on every message by caching embeddings, content-keyed, LRU-bounded, and persisted across restarts — behind a transparent `IEmbedder` decorator.

**Architecture:** A `CachingEmbedder` decorator wraps the real `OllamaEmbedder` and is registered as the `IEmbedder` DI singleton in its place, so all four match sites (topic/task/note/person, all routed through `IEmbedder.Embed`) benefit with no change to `Steps.fs`/`Pipeline.fs`. A pure, unit-tested `LruCache` holds `key → vector`; an `IEmbeddingCacheStore` + `FileSystemEmbeddingCacheStore` persist it to a `.taskmeister/` JSON file, mirroring the existing scheduler/job stores.

**Tech Stack:** F# / .NET 10, System.Text.Json, System.Security.Cryptography (SHA-256), xUnit 2.9.2.

## Global Constraints

- **Build must pass before any task is considered done.** `dotnet build` → 0 errors, 0 warnings.
- **Run tests per-project** (solution-wide `dotnet test` CLR-crashes on this host under MSBuild node-reuse): `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`.
- **Persisted state records must be public** (not `private`/`internal`) and `[<CLIMutable>]` — System.Text.Json serializes a private type to `{}`. (See `fsharp-private-record-serialization`.)
- **Stores are best-effort:** a load/save failure is swallowed and never breaks embedding.
- **Caching is host-only:** wired in `src/Nameless.TaskList/Program.fs` DI. Eval harness and tests construct embedders directly and stay unchanged.
- F# compile order is significant: a file may only reference types defined in files **above** it in `Nameless.TaskList.Core.fsproj`.

---

### Task 1: Pure LRU cache (`EmbeddingCache.fs`)

A thread-safe bounded LRU map `key → float[]`. No I/O — the eviction policy is unit-testable in isolation.

**Files:**
- Create: `src/Nameless.TaskList.Core/EmbeddingCache.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (register the new file before `Adapters.fs`)
- Test: `tests/Nameless.TaskList.Core.Tests/EmbeddingCacheTests.fs` (+ register in the test fsproj)

**Interfaces:**
- Produces:
  - `type LruCache(capacity: int)` with members:
    - `member Capacity : int`
    - `member Count : int`
    - `member TryGet : key: string -> float[] option` (a hit refreshes recency)
    - `member Set : key: string * vector: float[] -> unit` (insert/update; evicts LRU when over capacity)
    - `member Snapshot : unit -> (string * float[]) list` (most-recently-used first)
    - `member Seed : entriesMruFirst: (string * float[]) list -> unit` (load helper)

- [ ] **Step 1: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/EmbeddingCacheTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.EmbeddingCacheTests

open Nameless.TaskList.Core
open Xunit

[<Fact>]
let ``TryGet returns the stored vector`` () =
    let c = LruCache(3)
    c.Set("a", [| 1.0; 2.0 |])
    Assert.Equal<float[]>([| 1.0; 2.0 |], (c.TryGet "a").Value)
    Assert.Equal(None, c.TryGet "missing")

[<Fact>]
let ``Set beyond capacity evicts the least-recently-used entry`` () =
    let c = LruCache(2)
    c.Set("a", [| 1.0 |])
    c.Set("b", [| 2.0 |])
    c.Set("c", [| 3.0 |])          // evicts "a" (LRU)
    Assert.Equal(2, c.Count)
    Assert.Equal(None, c.TryGet "a")
    Assert.True((c.TryGet "b").IsSome)
    Assert.True((c.TryGet "c").IsSome)

[<Fact>]
let ``TryGet refreshes recency so the untouched entry is evicted`` () =
    let c = LruCache(2)
    c.Set("a", [| 1.0 |])
    c.Set("b", [| 2.0 |])
    c.TryGet "a" |> ignore         // "a" is now most-recent; "b" is LRU
    c.Set("c", [| 3.0 |])          // evicts "b"
    Assert.True((c.TryGet "a").IsSome)
    Assert.Equal(None, c.TryGet "b")
    Assert.True((c.TryGet "c").IsSome)

[<Fact>]
let ``Set on an existing key updates the value without growing`` () =
    let c = LruCache(2)
    c.Set("a", [| 1.0 |])
    c.Set("a", [| 9.0 |])
    Assert.Equal(1, c.Count)
    Assert.Equal<float[]>([| 9.0 |], (c.TryGet "a").Value)

[<Fact>]
let ``Snapshot is most-recently-used first and Seed round-trips it`` () =
    let c = LruCache(3)
    c.Set("a", [| 1.0 |])
    c.Set("b", [| 2.0 |])          // order: b, a
    Assert.Equal<string list>([ "b"; "a" ], c.Snapshot() |> List.map fst)
    let restored = LruCache(3)
    restored.Seed(c.Snapshot())
    Assert.Equal<string list>([ "b"; "a" ], restored.Snapshot() |> List.map fst)

[<Fact>]
let ``capacity is clamped to at least 1`` () =
    let c = LruCache(0)
    Assert.Equal(1, c.Capacity)
    c.Set("a", [| 1.0 |])
    c.Set("b", [| 2.0 |])
    Assert.Equal(1, c.Count)
```

Register it in `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj` by adding, after the `JobStoreTests.fs` line:

```xml
        <Compile Include="EmbeddingCacheTests.fs" />
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~EmbeddingCacheTests"`
Expected: FAIL — build error, `LruCache` is not defined.

- [ ] **Step 3: Create `EmbeddingCache.fs`**

```fsharp
namespace Nameless.TaskList.Core

open System.Collections.Generic

/// Thread-safe bounded LRU cache: content key -> embedding vector.
/// The match-site document embeds are a small, hot set (touched every message) so they never
/// evict; one-shot query embeds churn through the LRU tail. Pure of I/O so the eviction policy
/// is unit-testable in isolation.
type LruCache(capacity: int) =
    let cap = max 1 capacity
    let sync = obj ()
    // Most-recently-used at the front (First); least-recently-used at the back (Last).
    let order = LinkedList<string * float[]>()
    let index = Dictionary<string, LinkedListNode<string * float[]>>()

    let touch (node: LinkedListNode<string * float[]>) =
        order.Remove node
        order.AddFirst node

    member _.Capacity = cap

    member _.Count = lock sync (fun () -> index.Count)

    member _.TryGet(key: string) : float[] option =
        lock sync (fun () ->
            match index.TryGetValue key with
            | true, node ->
                touch node
                Some(snd node.Value)
            | _ -> None)

    member _.Set(key: string, vector: float[]) : unit =
        lock sync (fun () ->
            match index.TryGetValue key with
            | true, node ->
                node.Value <- (key, vector)
                touch node
            | _ ->
                let node = order.AddFirst((key, vector))
                index.[key] <- node
                if index.Count > cap then
                    let lru = order.Last
                    order.RemoveLast()
                    index.Remove(fst lru.Value) |> ignore)

    /// Entries most-recently-used first.
    member _.Snapshot() : (string * float[]) list =
        lock sync (fun () -> [ for kv in order -> kv ])

    /// Seed from a most-recently-used-first list (used on load).
    member this.Seed(entriesMruFirst: (string * float[]) list) : unit =
        for (k, v) in List.rev entriesMruFirst do
            this.Set(k, v)
```

Register it in `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` immediately **before** the `Adapters.fs` line:

```xml
        <Compile Include="EmbeddingCache.fs" />
        <Compile Include="Adapters.fs" />
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~EmbeddingCacheTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Nameless.TaskList.Core/EmbeddingCache.fs \
        src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj \
        tests/Nameless.TaskList.Core.Tests/EmbeddingCacheTests.fs \
        tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj
git commit -m "feat: pure LRU cache for embeddings"
```

---

### Task 2: Persisted state record, port, and filesystem store

**Files:**
- Modify: `src/Nameless.TaskList.Core/Library.fs` (add the two state records near the other persisted-state records, e.g. after `SchedulerState`)
- Modify: `src/Nameless.TaskList.Core/Ports.fs` (add `IEmbeddingCacheStore`)
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (add `FileSystemEmbeddingCacheStore`, after `FileSystemSchedulerStateStore`)
- Test: `tests/Nameless.TaskList.Core.Tests/EmbeddingCacheTests.fs` (append store tests)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces:
  - `[<CLIMutable>] type EmbeddingCacheEntry = { Key: string; Vector: float[] }`
  - `[<CLIMutable>] type EmbeddingCacheState = { Model: string; Entries: EmbeddingCacheEntry[] }`
  - `type IEmbeddingCacheStore` with `Load : unit -> EmbeddingCacheState` and `Save : EmbeddingCacheState -> unit`
  - `type FileSystemEmbeddingCacheStore(path: string)` implementing `IEmbeddingCacheStore`

- [ ] **Step 1: Write the failing tests**

Append to `tests/Nameless.TaskList.Core.Tests/EmbeddingCacheTests.fs`:

```fsharp
open System.IO
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Core.Ports

[<Fact>]
let ``FileSystemEmbeddingCacheStore round-trips state through a JSON file`` () =
    let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "embedding-cache.json")
    let store = FileSystemEmbeddingCacheStore(path) :> IEmbeddingCacheStore
    let state =
        { Model = "nomic-embed-text"
          Entries = [| { Key = "k1"; Vector = [| 0.1; 0.2 |] }
                       { Key = "k2"; Vector = [| 0.3 |] } |] }
    store.Save state
    let loaded = store.Load()
    Assert.Equal("nomic-embed-text", loaded.Model)
    Assert.Equal(2, loaded.Entries.Length)
    Assert.Equal("k1", loaded.Entries.[0].Key)
    Assert.Equal<float[]>([| 0.1; 0.2 |], loaded.Entries.[0].Vector)

[<Fact>]
let ``FileSystemEmbeddingCacheStore Load returns empty state when the file is missing`` () =
    let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "nope.json")
    let store = FileSystemEmbeddingCacheStore(path) :> IEmbeddingCacheStore
    let loaded = store.Load()
    Assert.Equal("", loaded.Model)
    Assert.Empty(loaded.Entries)

[<Fact>]
let ``FileSystemEmbeddingCacheStore Load returns empty state on a corrupt file`` () =
    let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
    File.WriteAllText(path, "{ not json")
    let store = FileSystemEmbeddingCacheStore(path) :> IEmbeddingCacheStore
    Assert.Empty((store.Load()).Entries)
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~EmbeddingCacheTests"`
Expected: FAIL — build error, `EmbeddingCacheState` / `FileSystemEmbeddingCacheStore` not defined.

- [ ] **Step 3: Add the records to `Library.fs`**

In `src/Nameless.TaskList.Core/Library.fs`, after the existing `SchedulerState` record (alongside the other persisted-state records), add:

```fsharp
/// One persisted embedding-cache entry: the content key (SHA-256 hex) and its vector.
/// Serialized to JSON — keep public.
[<CLIMutable>]
type EmbeddingCacheEntry = { Key: string; Vector: float[] }

/// The persisted embedding cache: the embed model the vectors were produced with (a model
/// change invalidates the file) plus the entries, most-recently-used first. JSON — keep public.
[<CLIMutable>]
type EmbeddingCacheState = { Model: string; Entries: EmbeddingCacheEntry[] }
```

- [ ] **Step 4: Add the port to `Ports.fs`**

In `src/Nameless.TaskList.Core/Ports.fs`, after `ISchedulerStateStore`, add:

```fsharp
/// Persists the embedding cache (content-keyed vectors) across restarts.
type IEmbeddingCacheStore =
    abstract member Load : unit -> EmbeddingCacheState
    abstract member Save : state: EmbeddingCacheState -> unit
```

- [ ] **Step 5: Add the adapter to `Adapters.fs`**

In `src/Nameless.TaskList.Core/Adapters.fs`, after `FileSystemSchedulerStateStore`, add:

```fsharp
    // ---- Embedding cache over a single JSON file. ----
    type FileSystemEmbeddingCacheStore(path: string) =
        interface IEmbeddingCacheStore with
            member _.Save(state) =
                let dir = Path.GetDirectoryName(path)
                if not (String.IsNullOrEmpty dir) then Directory.CreateDirectory(dir) |> ignore
                File.WriteAllText(path, JsonSerializer.Serialize(state))
            member _.Load() =
                try
                    if File.Exists path then
                        match JsonSerializer.Deserialize<EmbeddingCacheState>(File.ReadAllText path) with
                        | s when not (obj.ReferenceEquals(s.Entries, null)) -> s
                        | _ -> { Model = ""; Entries = [||] }
                    else { Model = ""; Entries = [||] }
                with _ -> { Model = ""; Entries = [||] }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~EmbeddingCacheTests"`
Expected: PASS (9 tests total — 6 from Task 1 + 3 store tests).

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList.Core/Library.fs \
        src/Nameless.TaskList.Core/Ports.fs \
        src/Nameless.TaskList.Core/Adapters.fs \
        tests/Nameless.TaskList.Core.Tests/EmbeddingCacheTests.fs
git commit -m "feat: persisted embedding-cache state, port, and filesystem store"
```

---

### Task 3: `CachingEmbedder` decorator

**Files:**
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (add `CachingEmbedder`, after `FileSystemEmbeddingCacheStore`)
- Test: `tests/Nameless.TaskList.Core.Tests/CachingEmbedderTests.fs` (+ register in the test fsproj)

**Interfaces:**
- Consumes: `LruCache` (Task 1); `IEmbeddingCacheStore`, `EmbeddingCacheState`, `EmbeddingCacheEntry` (Task 2); `IEmbedder` (`Ports.fs`); `FakeEmbedder(embed: string -> float array)` (`tests/.../Fakes.fs`).
- Produces:
  - `type CachingEmbedder(inner: IEmbedder, cache: LruCache, store: IEmbeddingCacheStore, model: string, saveEveryN: int)` implementing `IEmbedder`, plus `member Flush : unit -> unit`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/CachingEmbedderTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.CachingEmbedderTests

open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

/// In-memory store: records what was saved and serves a preset state on Load.
type private FakeCacheStore(initial: EmbeddingCacheState) =
    let mutable saved : EmbeddingCacheState option = None
    member _.Saved = saved
    interface IEmbeddingCacheStore with
        member _.Load() = initial
        member _.Save(state) = saved <- Some state

let private empty : EmbeddingCacheState = { Model = ""; Entries = [||] }

[<Fact>]
let ``identical text is embedded once (cache hit on the second call)`` () =
    let mutable calls = 0
    let inner = FakeEmbedder(fun _ -> calls <- calls + 1; [| 1.0 |]) :> IEmbedder
    let ce = CachingEmbedder(inner, LruCache(10), FakeCacheStore(empty), "m", 1000) :> IEmbedder
    ce.Embed "hello" |> ignore
    ce.Embed "hello" |> ignore
    Assert.Equal(1, calls)

[<Fact>]
let ``distinct text embeds each time`` () =
    let mutable calls = 0
    let inner = FakeEmbedder(fun _ -> calls <- calls + 1; [| 1.0 |]) :> IEmbedder
    let ce = CachingEmbedder(inner, LruCache(10), FakeCacheStore(empty), "m", 1000) :> IEmbedder
    ce.Embed "a" |> ignore
    ce.Embed "b" |> ignore
    Assert.Equal(2, calls)

[<Fact>]
let ``an evicted entry is re-embedded`` () =
    let mutable calls = 0
    let inner = FakeEmbedder(fun _ -> calls <- calls + 1; [| 1.0 |]) :> IEmbedder
    let ce = CachingEmbedder(inner, LruCache(1), FakeCacheStore(empty), "m", 1000) :> IEmbedder
    ce.Embed "a" |> ignore     // calls = 1
    ce.Embed "b" |> ignore     // calls = 2, evicts "a"
    ce.Embed "a" |> ignore     // calls = 3, "a" was evicted
    Assert.Equal(3, calls)

[<Fact>]
let ``the cache is persisted after SaveEveryN misses`` () =
    let inner = FakeEmbedder(fun _ -> [| 1.0 |]) :> IEmbedder
    let store = FakeCacheStore(empty)
    let ce = CachingEmbedder(inner, LruCache(10), store, "m", 2) :> IEmbedder
    ce.Embed "a" |> ignore     // miss 1 — no save yet
    Assert.True(store.Saved.IsNone)
    ce.Embed "b" |> ignore     // miss 2 — triggers a save
    Assert.True(store.Saved.IsSome)
    Assert.Equal(2, store.Saved.Value.Entries.Length)
    Assert.Equal("m", store.Saved.Value.Model)

[<Fact>]
let ``a persisted cache for the same model is loaded and serves hits`` () =
    // Warm a decorator so the store captures a real (hashed) key for "shared", then reload it.
    let mutable calls = 0
    let inner = FakeEmbedder(fun _ -> calls <- calls + 1; [| 7.0 |]) :> IEmbedder
    let store1 = FakeCacheStore(empty)
    let warm = CachingEmbedder(inner, LruCache(10), store1, "m", 1) :> IEmbedder
    warm.Embed "shared" |> ignore                     // calls = 1, store1 saved
    let saved = store1.Saved.Value
    // New decorator loads the saved state -> "shared" must be a hit (no new inner call).
    let cold = CachingEmbedder(inner, LruCache(10), FakeCacheStore(saved), "m", 1000) :> IEmbedder
    cold.Embed "shared" |> ignore
    Assert.Equal(1, calls)

[<Fact>]
let ``a persisted cache for a different model is discarded`` () =
    let mutable calls = 0
    let inner = FakeEmbedder(fun _ -> calls <- calls + 1; [| 7.0 |]) :> IEmbedder
    let store1 = FakeCacheStore(empty)
    let warm = CachingEmbedder(inner, LruCache(10), store1, "old-model", 1) :> IEmbedder
    warm.Embed "shared" |> ignore                     // calls = 1
    let saved = store1.Saved.Value
    let cold = CachingEmbedder(inner, LruCache(10), FakeCacheStore(saved), "new-model", 1000) :> IEmbedder
    cold.Embed "shared" |> ignore                     // model differs -> not loaded -> re-embed
    Assert.Equal(2, calls)
```

Register it in `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`, after the `EmbeddingCacheTests.fs` line:

```xml
        <Compile Include="CachingEmbedderTests.fs" />
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~CachingEmbedderTests"`
Expected: FAIL — build error, `CachingEmbedder` not defined.

- [ ] **Step 3: Add `CachingEmbedder` to `Adapters.fs`**

In `src/Nameless.TaskList.Core/Adapters.fs`, after `FileSystemEmbeddingCacheStore`, add:

```fsharp
    // ---- Caching decorator over any IEmbedder. Content-keyed by SHA-256 of (model + text) so a
    //      change of embed model can't serve stale vectors; LRU-bounded; persisted best-effort. ----
    type CachingEmbedder(inner: IEmbedder, cache: LruCache, store: IEmbeddingCacheStore, model: string, saveEveryN: int) =
        let sync = obj ()
        let n = max 1 saveEveryN
        let mutable dirty = 0

        let keyFor (text: string) =
            use sha = System.Security.Cryptography.SHA256.Create()
            let bytes = System.Text.Encoding.UTF8.GetBytes(model + "\n" + text)
            sha.ComputeHash bytes |> Array.map (fun b -> b.ToString("x2")) |> String.concat ""

        let flush () =
            try
                let entries =
                    cache.Snapshot()
                    |> List.map (fun (k, v) -> { Key = k; Vector = v })
                    |> List.toArray
                store.Save { Model = model; Entries = entries }
            with _ -> ()

        do
            // Load the persisted cache; discard it if produced by a different embed model.
            try
                let st = store.Load()
                if not (obj.ReferenceEquals(st, null)) && st.Model = model
                   && not (obj.ReferenceEquals(st.Entries, null)) then
                    cache.Seed [ for e in st.Entries -> (e.Key, e.Vector) ]
            with _ -> ()

        /// Persist the current cache (best-effort). Wired to ApplicationStopping in the host.
        member _.Flush() = lock sync flush

        interface IEmbedder with
            member _.Embed(text) =
                let key = keyFor text
                match cache.TryGet key with
                | Some v -> v
                | None ->
                    let v = inner.Embed text
                    cache.Set(key, v)
                    let shouldFlush =
                        lock sync (fun () ->
                            dirty <- dirty + 1
                            if dirty >= n then dirty <- 0; true else false)
                    if shouldFlush then lock sync flush
                    v
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~CachingEmbedderTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Nameless.TaskList.Core/Adapters.fs \
        tests/Nameless.TaskList.Core.Tests/CachingEmbedderTests.fs \
        tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj
git commit -m "feat: CachingEmbedder decorator over IEmbedder"
```

---

### Task 4: Wire the decorator into the host + config

The decorator is constructed in DI in place of the bare `OllamaEmbedder` (unless disabled), and flushed on graceful shutdown. No unit test (the host `Program.fs` is not unit-tested, matching how the scheduler/jobs wiring is verified); the deliverable is a clean build plus the full Core suite still green.

**Files:**
- Modify: `src/Nameless.TaskList/appsettings.json` (add the `EmbeddingCache` section)
- Modify: `src/Nameless.TaskList/Program.fs` (rewire the `IEmbedder` singleton; add the shutdown flush)

**Interfaces:**
- Consumes: `LruCache`, `FileSystemEmbeddingCacheStore`, `CachingEmbedder`, `IEmbeddingCacheStore` (Tasks 1–3); `OllamaEmbedder`, `IEmbedder` (existing); `Microsoft.Extensions.Hosting.IHostApplicationLifetime` (already in scope via `open Microsoft.Extensions.Hosting`).

- [ ] **Step 1: Add the config section**

In `src/Nameless.TaskList/appsettings.json`, add this entry as a sibling of `"BulkJobs"` (insert after the `"BulkJobs": { ... }` block):

```json
  "EmbeddingCache": {
    "Enabled": true,
    "MaxEntries": 2000,
    "SaveEveryN": 50,
    "StatePath": ""
  },
```

- [ ] **Step 2: Rewire the `IEmbedder` registration**

In `src/Nameless.TaskList/Program.fs`, replace the existing block:

```fsharp
        builder.Services.AddSingleton<IEmbedder>(fun sp ->
            let http = sp.GetRequiredService<HttpClient>()
            let embedModel = if isNull cfg.["Ollama:EmbedModel"] then "nomic-embed-text" else cfg.["Ollama:EmbedModel"]
            OllamaEmbedder(http, cfg.["Ollama:Url"], embedModel) :> IEmbedder) |> ignore
```

with:

```fsharp
        builder.Services.AddSingleton<IEmbedder>(fun sp ->
            let http = sp.GetRequiredService<HttpClient>()
            let embedModel = if isNull cfg.["Ollama:EmbedModel"] then "nomic-embed-text" else cfg.["Ollama:EmbedModel"]
            let inner = OllamaEmbedder(http, cfg.["Ollama:Url"], embedModel) :> IEmbedder
            // Disabled (explicit "false") -> bare embedder, today's behaviour.
            if cfg.["EmbeddingCache:Enabled"] = "false" then inner
            else
                let maxEntries = match System.Int32.TryParse(cfg.["EmbeddingCache:MaxEntries"]) with | true, v when v > 0 -> v | _ -> 2000
                let saveEveryN = match System.Int32.TryParse(cfg.["EmbeddingCache:SaveEveryN"]) with | true, v when v > 0 -> v | _ -> 50
                let statePath =
                    let configured = cfg.["EmbeddingCache:StatePath"]
                    if System.String.IsNullOrWhiteSpace configured
                    then System.IO.Path.Combine(cfg.["Vault:Root"], ".taskmeister", "embedding-cache.json")
                    else configured
                let store = FileSystemEmbeddingCacheStore(statePath) :> IEmbeddingCacheStore
                CachingEmbedder(inner, LruCache(maxEntries), store, embedModel, saveEveryN) :> IEmbedder) |> ignore
```

- [ ] **Step 3: Add the shutdown flush**

In `src/Nameless.TaskList/Program.fs`, immediately after `let app = builder.Build()`, add:

```fsharp
        // Persist the embedding cache on graceful shutdown (best-effort).
        match app.Services.GetRequiredService<IEmbedder>() with
        | :? CachingEmbedder as ce ->
            let lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>()
            lifetime.ApplicationStopping.Register(fun () -> ce.Flush()) |> ignore
        | _ -> ()
```

- [ ] **Step 4: Build the whole solution**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 5: Run the full Core test suite (regression)**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`
Expected: PASS — all existing tests plus the 12 new ones (6 LruCache+store-extended = 9 in `EmbeddingCacheTests`, 6 in `CachingEmbedderTests`); 0 failures.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList/appsettings.json src/Nameless.TaskList/Program.fs
git commit -m "feat: wire CachingEmbedder into host DI with config + shutdown flush"
```

---

## Self-Review

**Spec coverage:**
- Transparent decorator at `IEmbedder`, all four sites, no `Steps.fs`/`Pipeline.fs` change → Tasks 3 + 4 (decorator + DI swap). ✓
- Pure unit-tested LRU → Task 1. ✓
- `IEmbeddingCacheStore` + `FileSystemEmbeddingCacheStore` + `EmbeddingCacheState` → Task 2. ✓
- Key = SHA-256 of `model + "\n" + text` → Task 3 `keyFor`. ✓
- Model-mismatch discard on load → Task 3 `do` block + `different model is discarded` test. ✓
- LRU `MaxEntries` bounding → Task 1 eviction + Task 4 config. ✓
- Debounced persistence (`SaveEveryN`) + shutdown flush, best-effort → Task 3 (`flush`/dirty counter) + Task 4 Step 3. ✓
- Config block with defaults + `Enabled:false` escape hatch → Task 4 Steps 1–2. ✓
- Host-only; eval/tests unchanged → only `Program.fs` wires it; tests build embedders directly. ✓
- Testing matrix (pure LRU, decorator, store) → Tasks 1–3 tests. ✓

**Placeholder scan:** No TBD/TODO; every code step is complete. The `ignore preset` line in the load-hit test is deliberate (keeps the test self-contained without an unused-binding warning).

**Type consistency:** `LruCache` members (`TryGet`/`Set`/`Snapshot`/`Seed`/`Count`/`Capacity`) are used identically in the decorator and tests. `EmbeddingCacheState`/`EmbeddingCacheEntry` field names (`Model`/`Entries`/`Key`/`Vector`) match across Library, store, decorator, and tests. `IEmbeddingCacheStore.Load/Save` signatures match the adapter and the `FakeCacheStore`. `CachingEmbedder` constructor arity `(inner, cache, store, model, saveEveryN)` matches every construction site (tests + DI).

## Out of Scope (per spec)

Caching chat/vision/transcription; cache warming/precompute; cross-process/networked cache.
