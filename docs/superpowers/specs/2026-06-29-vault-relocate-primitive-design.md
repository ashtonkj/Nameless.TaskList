# Vault relocate primitive — design

**Date:** 2026-06-29
**Status:** Approved (ready for plan)
**Roadmap item:** Medium — foundation for "Person context re-filing + retroactive entity de-dup" (the user chose to design the shared relocate/retire mechanism first, as its own mini-cycle).

## Problem

Two upcoming features need to **vacate an active vault path**: context re-filing moves a person file from one
context folder to another (`people/family/x.md` → `people/medical/x.md`), and retroactive de-dup retires a
redundant duplicate after merging it into the canonical. The vault is **append-only** (`IVault` exposes only
`Exists`/`Read`/`Write`/`ListFiles`/`ListFilesRecursive`; `FileSystemVault` never deletes), so neither move is
expressible today — a naive `Write` to the new path leaves the old file behind as a *new* duplicate that
`peopleIndex`/`resolvePerson`/`reindex` still see.

The never-delete rule (DESIGN §"Behaviour", line 675: "Never delete files — set status to cancelled or
resolved") means **don't lose data**, not "never move": DESIGN's own §9 Archive policy *moves* files into
`archive/` subdirs. So a relocation that preserves the bytes is consistent with both.

## Goal

Add one foundational primitive — relocate a vault file, preserving its bytes — plus a `.trash/` retire
convenience, so the two consumer features can move/retire files without violating never-delete. This spec ships
the **capability only**; nothing triggers a relocate yet (the re-filing and de-dup specs are separate cycles).

## Approach: one `Relocate` capability on `IVault`

Add a single method to the `IVault` port:

```fsharp
abstract member Relocate : src: string * dst: string -> unit
```

A physical move that preserves exact bytes (frontmatter *and* body), creating `dst`'s parent directories. Both
consumer features reduce to **(write/merge the new path) + (relocate the old path)**:

- **Re-filing:** `Relocate(oldCtxPath, newCtxPath)` — active→active move.
- **De-dup retire:** `Relocate(dupPath, trashPath)` — active→`.trash/` move, after the merge has written the
  canonical.

**Rejected alternative:** a separate `IArchiver` port — relocation is fundamentally a vault file operation and
`IVault` already owns `Write`/`Exists`/`ListFiles` and the never-delete contract, so the capability is cohesive
there. The cost is that both `IVault` implementers (`FileSystemVault`, the test `FakeVault`) add the method —
trivial.

**Why not `Retire(path)` only:** re-filing is active→active, not active→`.trash/`, so a general
`Relocate(src,dst)` expresses both moves without leaking the `.trash/` convention into the port.

## Components

1. **`IVault.Relocate` (`Ports.fs`)** — the new abstract method `Relocate : src:string * dst:string -> unit`.

2. **`FileSystemVault.Relocate` (`Adapters.fs`)** — `File.Move(full src, full dst)` after
   `Directory.CreateDirectory(dst parent)`. **Best-effort & replay-safe:**
   - `src` does not exist → no-op (a replay / double-retire is harmless).
   - `dst` already exists → disambiguate (append a numeric counter to the destination, see §`.trash/`) rather
     than overwrite — never destroy data at `dst`.
   - Any IO failure is swallowed (best-effort, like the vault's other writes); it never throws into a
     pipeline/reindex run.

3. **`FakeVault.Relocate` (`tests/.../Fakes.fs`)** — rename the dictionary key (remove `src`, add `dst` with the
   same content); `src`-missing → no-op; `dst`-exists → disambiguate. Mirrors the real semantics so unit tests
   exercise the contract.

4. **`Naming.trashPath` (`KnowledgeBase.fs`, pure)** — `trashPath : (ts:System.DateTime) -> (relPath:string) ->
   string`. Maps an active relPath to its `.trash/` destination with a timestamp inserted before the extension:
   `trashPath ts "people/family/jane.md"` → `.trash/people/family/jane-20260629T145501.md`. Pure (timestamp is a
   parameter, not read internally) → deterministically unit-testable.

5. **`Vault.retire` (small Core helper, new `VaultOps.fs` after `Ports.fs`)** — `retire : IVault ->
   System.DateTime -> string -> unit`, defined as `vault.Relocate(path, Naming.trashPath ts path)`. DRY
   convenience both consumer features call; keeps the `.trash/` convention in exactly one place. The caller
   passes `(Time.now offset).DateTime` so the timestamp uses the configured KB offset, consistent with the rest
   of the KB.

## The `.trash/` convention

- Retired files mirror their original path under a top-level `.trash/` directory, with a timestamp
  disambiguator: `.trash/<original-dir>/<name>-<yyyyMMddTHHmmss>.<ext>`.
- `.trash/` is a **dot-folder**, so Obsidian ignores it (retired files leave the graph) and it sits **outside**
  the `people/`/`tasks/`/`notes/`/… scan roots — so `peopleIndex`, `resolvePerson`, the Indexer, and every
  other `ListFiles`/`ListFilesRecursive` consumer stop seeing retired files automatically. **No
  tombstone-skipping logic is needed anywhere.**
- Collision guard: the timestamp normally disambiguates; if the timestamped path still exists (same-second
  repeat), `Relocate` appends `-2`, `-3`, … before the extension.

## Error handling

All operations are best-effort and idempotent: `src` missing → no-op (replay/double-retire safe); `dst`
collision → never overwrites (timestamp + numeric-suffix guard); a failed `File.Move` is swallowed and logged,
never breaking the run — consistent with the vault's existing best-effort writes.

## Testing

- **`Naming.trashPath` (pure, `NamingTests`):** mirrors the path under `.trash/`; inserts the timestamp before
  the extension; handles a nested path; handles a name with no extension; a fixed `ts` yields the expected
  string.
- **`FakeVault.Relocate` round-trip (`VaultTests`):** after relocate, `Exists src` is false, `Exists dst` is
  true, and `Read dst` equals the original content; `src`-missing → no-op (no throw, no dst created);
  `dst`-exists → the existing dst is preserved and the move lands on a disambiguated path.
- **`Vault.retire` helper (`VaultTests`):** retiring `people/family/jane.md` vacates the original path and the
  content lands under `.trash/people/family/…`.
- **`FileSystemVault.Relocate`** — covered by the opt-in integration suite (real `File.Move` into a temp-vault
  `.trash/`), alongside the other filesystem-adapter tests. (`FileSystemVault` is integration-only by design;
  not unit-tested.)

## File / compile-order summary

- `Ports.fs` — add `Relocate` to `IVault`.
- `KnowledgeBase.fs` — add `Naming.trashPath` (pure).
- `VaultOps.fs` — **new**, after `Ports.fs`: `module Vault` with `retire`.
- `Adapters.fs` — implement `FileSystemVault.Relocate`.
- `Nameless.TaskList.Core.fsproj` — register `VaultOps.fs` after `Ports.fs`.
- `tests/.../Fakes.fs` — implement `FakeVault.Relocate`.
- Tests: `NamingTests.fs`, `VaultTests.fs`; integration test for the FS adapter.

## Out of scope (future, separate cycles)

- **The consumer features** themselves — person context re-filing, retroactive entity de-dup. They are designed
  and built next, on top of this primitive.
- **A `.trash/` purge/restore policy** — retired files accumulate; pruning/restoring `.trash/` is a later
  concern (and overlaps the DESIGN §9 Archive policy work). Not needed for the primitive.
- **The DESIGN §9 time-based Archive policy** (`messages/archive/{year}/` etc.) — a distinct feature; this
  primitive could underpin it later but does not implement it.
