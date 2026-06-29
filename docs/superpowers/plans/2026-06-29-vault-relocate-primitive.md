# Vault Relocate Primitive Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a byte-preserving `IVault.Relocate` move primitive plus a `.trash/` retire helper, so the upcoming person re-filing and de-dup features can vacate active vault paths without violating the never-delete rule.

**Architecture:** One new method `Relocate(src, dst)` on the `IVault` port (a physical move, guarded so it never overwrites a destination or throws on a missing source); a pure `Naming.trashPath` that maps an active path to a timestamped `.trash/` destination; and a tiny `Vault.retire` Core helper composing the two. Ships the capability only — no feature triggers a relocate yet.

**Tech Stack:** F# / .NET 10, xUnit 2.9.2.

## Global Constraints

- **Build must pass before any task is done:** `dotnet build` → 0 errors, 0 warnings.
- **Run tests per-project** (solution-wide `dotnet test` CLR-crashes under MSBuild node-reuse on this host): `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1` and `dotnet test tests/Nameless.TaskList.Tests -p:nodeReuse=false -maxcpucount:1`.
- **The `.trash/` convention:** retired files mirror their original path under a top-level `.trash/` dir with a timestamp inserted before the extension: `.trash/<dir>/<name>-<yyyyMMddTHHmmss><ext>`. `.trash/` is a dot-folder (Obsidian ignores it; outside every `people/`/`tasks/`… scan root).
- **Relocate contract:** `src` missing → no-op; `dst` already exists → no-op (never overwrite, leave `src`); otherwise move bytes `src`→`dst`, vacating `src`.
- **Adding a method to `IVault` breaks every implementer until updated.** There are exactly FOUR: `FileSystemVault` (`Adapters.fs`), `FakeVault` (`tests/Nameless.TaskList.Core.Tests/Fakes.fs`), the inline `FakeVault` (`tests/Nameless.TaskList.Tests/EndpointTests.fs`), and `InMemoryVault` (`eval/Nameless.TaskList.Eval/Worlds.fs`). All four must gain `Relocate` in the same task or the solution won't compile.
- F# compile order is significant: a file may only reference types defined in files above it in `Nameless.TaskList.Core.fsproj`.

---

### Task 1: `Naming.trashPath` (pure)

Maps an active vault path to its `.trash/` destination with a timestamp disambiguator. Pure — the timestamp is a parameter, not read internally.

**Files:**
- Modify: `src/Nameless.TaskList.Core/KnowledgeBase.fs` (add `trashPath` in the `Naming` module, after `digestPath`)
- Test: `tests/Nameless.TaskList.Core.Tests/NamingTests.fs` (append)

**Interfaces:**
- Produces: `Naming.trashPath : ts:System.DateTime -> relPath:string -> string`

- [ ] **Step 1: Write the failing tests**

Append to `tests/Nameless.TaskList.Core.Tests/NamingTests.fs`:

```fsharp
[<Fact>]
let ``trashPath mirrors a nested path under .trash with a timestamp before the extension`` () =
    let ts = System.DateTime(2026, 6, 29, 14, 55, 1)
    Assert.Equal(".trash/people/family/jane-20260629T145501.md", Naming.trashPath ts "people/family/jane.md")

[<Fact>]
let ``trashPath handles a root-level file`` () =
    let ts = System.DateTime(2026, 6, 29, 14, 55, 1)
    Assert.Equal(".trash/x-20260629T145501.md", Naming.trashPath ts "x.md")

[<Fact>]
let ``trashPath handles a name with no extension`` () =
    let ts = System.DateTime(2026, 6, 29, 14, 55, 1)
    Assert.Equal(".trash/people/a-20260629T145501", Naming.trashPath ts "people/a")
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~NamingTests.trashPath"`
Expected: FAIL — build error, `Naming.trashPath` is not defined.

- [ ] **Step 3: Add `trashPath` to the `Naming` module**

In `src/Nameless.TaskList.Core/KnowledgeBase.fs`, inside `module Naming`, after the `digestPath` function, add:

```fsharp
    /// Destination for a retired file: mirror its path under a top-level .trash/ directory,
    /// inserting a timestamp before the extension so repeated retires never collide. Pure —
    /// the timestamp is supplied by the caller (callers pass the configured-offset wall clock).
    let trashPath (ts: System.DateTime) (relPath: string) : string =
        let p = (if isNull relPath then "" else relPath).Replace('\\', '/')
        let dir = System.IO.Path.GetDirectoryName(p)
        let name = System.IO.Path.GetFileNameWithoutExtension(p)
        let ext = System.IO.Path.GetExtension(p)
        let stamp = ts.ToString("yyyyMMddTHHmmss")
        let dirPart =
            if System.String.IsNullOrEmpty dir then "" else dir.Replace('\\', '/') + "/"
        sprintf ".trash/%s%s-%s%s" dirPart name stamp ext
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~NamingTests.trashPath"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Nameless.TaskList.Core/KnowledgeBase.fs tests/Nameless.TaskList.Core.Tests/NamingTests.fs
git commit -m "feat: Naming.trashPath maps an active path to a timestamped .trash/ destination"
```

---

### Task 2: `IVault.Relocate` — port method + all four implementations

Adds the move primitive to the port and every implementer. This is one atomic change (the solution won't compile until all four implementers have it).

**Files:**
- Modify: `src/Nameless.TaskList.Core/Ports.fs` (add `Relocate` to `IVault`)
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (`FileSystemVault.Relocate`)
- Modify: `tests/Nameless.TaskList.Core.Tests/Fakes.fs` (`FakeVault.Relocate`)
- Modify: `tests/Nameless.TaskList.Tests/EndpointTests.fs` (inline `FakeVault.Relocate`)
- Modify: `eval/Nameless.TaskList.Eval/Worlds.fs` (`InMemoryVault.Relocate`)
- Test: `tests/Nameless.TaskList.Core.Tests/VaultTests.fs` (append)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `IVault.Relocate : src:string * dst:string -> unit` (member on all four implementers).

- [ ] **Step 1: Write the failing tests**

Append to `tests/Nameless.TaskList.Core.Tests/VaultTests.fs`:

```fsharp
[<Fact>]
let ``Relocate moves a file, vacating the source`` () =
    let root = tempRoot ()
    try
        let vault = FileSystemVault(root) :> IVault
        vault.Write("people/family/jane.md", "body")
        vault.Relocate("people/family/jane.md", ".trash/people/family/jane-20260629T145501.md")
        Assert.False(vault.Exists("people/family/jane.md"))
        Assert.True(vault.Exists(".trash/people/family/jane-20260629T145501.md"))
        Assert.Equal("body", vault.Read(".trash/people/family/jane-20260629T145501.md"))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``Relocate is a no-op when the source is missing`` () =
    let root = tempRoot ()
    try
        let vault = FileSystemVault(root) :> IVault
        vault.Relocate("nope.md", ".trash/nope.md")
        Assert.False(vault.Exists(".trash/nope.md"))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``Relocate does not overwrite an existing destination`` () =
    let root = tempRoot ()
    try
        let vault = FileSystemVault(root) :> IVault
        vault.Write("a.md", "AAA")
        vault.Write("b.md", "BBB")
        vault.Relocate("a.md", "b.md")
        Assert.True(vault.Exists("a.md"))        // src preserved (no-op)
        Assert.Equal("BBB", vault.Read("b.md"))  // dst untouched
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``FakeVault Relocate moves a file, vacating the source`` () =
    let vault = Nameless.TaskList.Core.Tests.Fakes.FakeVault()
    vault.Seed("people/family/jane.md", "body")
    (vault :> IVault).Relocate("people/family/jane.md", ".trash/people/family/jane-x.md")
    Assert.False((vault :> IVault).Exists("people/family/jane.md"))
    Assert.Equal("body", (vault :> IVault).Read(".trash/people/family/jane-x.md"))
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~VaultTests.Relocate"`
Expected: FAIL — build error, `IVault` has no member `Relocate`.

- [ ] **Step 3: Add `Relocate` to the `IVault` port**

In `src/Nameless.TaskList.Core/Ports.fs`, inside the `IVault` type, add the method (after `ListFilesRecursive`):

```fsharp
    /// Move a file from one vault-relative path to another, preserving its bytes. Never deletes
    /// data: src missing -> no-op; dst already exists -> no-op (never overwrite); otherwise the
    /// bytes move src -> dst and src is vacated.
    abstract member Relocate : src: string * dst: string -> unit
```

- [ ] **Step 4: Implement `FileSystemVault.Relocate`**

In `src/Nameless.TaskList.Core/Adapters.fs`, inside `FileSystemVault`'s `interface IVault with`, after the `ListFilesRecursive` member, add:

```fsharp
            member _.Relocate(src, dst) =
                let s = full src
                let d = full dst
                // Guards keep the contract (no overwrite, no throw on a missing source). A genuine
                // IO failure propagates, exactly like Write.
                if File.Exists s && not (File.Exists d) then
                    Directory.CreateDirectory(Path.GetDirectoryName(d)) |> ignore
                    File.Move(s, d)
```

- [ ] **Step 5: Implement `FakeVault.Relocate` (Core.Tests)**

In `tests/Nameless.TaskList.Core.Tests/Fakes.fs`, inside `FakeVault`'s `interface IVault with`, after `ListFilesRecursive`, add:

```fsharp
        member _.Relocate(src, dst) =
            match files.TryGetValue src with
            | true, content when not (files.ContainsKey dst) ->
                files.Remove src |> ignore
                files.[dst] <- content
            | _ -> ()   // src missing or dst exists -> no-op
```

- [ ] **Step 6: Implement the inline `FakeVault.Relocate` (endpoint tests)**

In `tests/Nameless.TaskList.Tests/EndpointTests.fs`, inside the inline `FakeVault`'s `interface IVault with`, after `ListFilesRecursive`, add the identical member:

```fsharp
        member _.Relocate(src, dst) =
            match files.TryGetValue src with
            | true, content when not (files.ContainsKey dst) ->
                files.Remove src |> ignore
                files.[dst] <- content
            | _ -> ()
```

- [ ] **Step 7: Implement `InMemoryVault.Relocate` (eval)**

In `eval/Nameless.TaskList.Eval/Worlds.fs`, inside `InMemoryVault`'s `interface IVault with`, after `ListFilesRecursive`, add:

```fsharp
            member _.Relocate(src, dst) =
                match files.TryGetValue src with
                | true, content when not (files.ContainsKey dst) ->
                    files.Remove src |> ignore
                    files.[dst] <- content
                | _ -> ()
```

- [ ] **Step 8: Build the whole solution (proves all four implementers compile)**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~VaultTests.Relocate"`
Expected: PASS (4 tests).

- [ ] **Step 10: Commit**

```bash
git add src/Nameless.TaskList.Core/Ports.fs src/Nameless.TaskList.Core/Adapters.fs \
        tests/Nameless.TaskList.Core.Tests/Fakes.fs tests/Nameless.TaskList.Tests/EndpointTests.fs \
        eval/Nameless.TaskList.Eval/Worlds.fs tests/Nameless.TaskList.Core.Tests/VaultTests.fs
git commit -m "feat: IVault.Relocate move primitive (port + all four implementations)"
```

---

### Task 3: `Vault.retire` helper

Composes `Relocate` + `trashPath` into a best-effort retire, so the consumer features call one function and the `.trash/` convention lives in one place.

**Files:**
- Create: `src/Nameless.TaskList.Core/VaultOps.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (register after `Ports.fs`)
- Test: `tests/Nameless.TaskList.Core.Tests/VaultTests.fs` (append)

**Interfaces:**
- Consumes: `Naming.trashPath` (Task 1), `IVault.Relocate` (Task 2).
- Produces: `Vault.retire : IVault -> System.DateTime -> string -> unit`

- [ ] **Step 1: Write the failing test**

Append to `tests/Nameless.TaskList.Core.Tests/VaultTests.fs`:

```fsharp
[<Fact>]
let ``Vault.retire moves the file under .trash, vacating the original`` () =
    let vault = Nameless.TaskList.Core.Tests.Fakes.FakeVault()
    vault.Seed("people/family/jane.md", "body")
    Nameless.TaskList.Core.Vault.retire (vault :> IVault) (System.DateTime(2026, 6, 29, 14, 55, 1)) "people/family/jane.md"
    Assert.False((vault :> IVault).Exists("people/family/jane.md"))
    Assert.True((vault :> IVault).Exists(".trash/people/family/jane-20260629T145501.md"))
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~VaultTests.retire"`
Expected: FAIL — build error, the `Vault` module / `retire` is not defined.

- [ ] **Step 3: Create `VaultOps.fs`**

Create `src/Nameless.TaskList.Core/VaultOps.fs`:

```fsharp
namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.Ports

/// Higher-level vault operations composed over the IVault primitives.
module Vault =

    /// Retire an active file to the .trash/ area (bytes preserved, original path vacated).
    /// Best-effort: a relocate failure (or a missing source) is swallowed so it never breaks
    /// the caller. `ts` is the configured-offset wall clock (callers pass (Time.now offset).DateTime).
    let retire (vault: IVault) (ts: System.DateTime) (relPath: string) : unit =
        try vault.Relocate(relPath, Naming.trashPath ts relPath) with _ -> ()
```

Register it in `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` immediately **after** the `Ports.fs` line:

```xml
        <Compile Include="Ports.fs" />
        <Compile Include="VaultOps.fs" />
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1 --filter "FullyQualifiedName~VaultTests.retire"`
Expected: PASS (1 test).

- [ ] **Step 5: Build + run the full Core and endpoint suites (regression)**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.
Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`
Expected: all pass (330 prior + 8 new = 338).
Run: `dotnet test tests/Nameless.TaskList.Tests -p:nodeReuse=false -maxcpucount:1`
Expected: all pass (18).

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/VaultOps.fs src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj \
        tests/Nameless.TaskList.Core.Tests/VaultTests.fs
git commit -m "feat: Vault.retire helper (best-effort relocate to .trash/)"
```

---

## Self-Review

**Spec coverage:**
- `IVault.Relocate` move primitive (byte-preserving, src-missing no-op, no overwrite) → Task 2. ✓
- `Naming.trashPath` pure helper → Task 1. ✓
- `Vault.retire` (`VaultOps.fs`) best-effort → Task 3. ✓
- `.trash/` dot-folder convention (path mirror + timestamp before ext) → Task 1 `trashPath`. ✓
- All four `IVault` implementers updated → Task 2 Steps 4–7. ✓
- Tests: `trashPath` pure cases, `FileSystemVault` round-trip + src-missing + dst-exists, `FakeVault` round-trip, `Vault.retire` → Tasks 1–3. ✓

**Deviations from the spec (intentional, flagged for confirmation):**
1. **`dst` exists → no-op, instead of "append a numeric counter".** Because `trashPath` mirrors the *full* original path and stamps the second, the only way two retires collide on one `dst` is retiring the *same* `src` twice in the same second — which is the idempotent-replay case where the second retire already finds `src` missing (→ no-op). A real two-different-srcs collision is unreachable, so the counter would be dead code. The simpler "no-op, never overwrite, leave src" contract is implemented uniformly across all four vaults and preserves the never-destroy-`dst` guarantee.
2. **`FileSystemVault.Relocate` does not blanket-swallow IO errors** (it only guards the two no-op conditions); a genuine IO failure propagates exactly like `Write`. Best-effort lives in `Vault.retire` (the `try/with`), which is what the consumer features call. This keeps the unit tests honest (a failed move surfaces) while still giving callers a swallow-safe entry point.
- The spec's note that the FS adapter is "integration-only" is superseded: `VaultTests.fs` already unit-tests `FileSystemVault` with temp dirs, so the `Relocate` FS tests live there (faster feedback, no integration gating).

**Placeholder scan:** No TBD/TODO; every code step is complete.

**Type consistency:** `Relocate(src, dst)` signature is identical across the port and all four implementers and every call site. `trashPath ts relPath` and `Vault.retire vault ts relPath` argument orders match between definition, tests, and the `retire` body. The `.trash/people/family/jane-20260629T145501.md` string is identical in the `trashPath` test, the `Relocate` FS test, and the `retire` test (all from `DateTime(2026,6,29,14,55,1)`).

## Out of Scope (per spec)

The consumer features (person re-filing, retroactive de-dup); a `.trash/` purge/restore policy; the DESIGN §9 time-based archive policy.
