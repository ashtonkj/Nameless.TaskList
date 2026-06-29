# Eval / Steps Robustness Polish — Design

**Date:** 2026-06-29
**Status:** Approved (brainstorm) → ready for implementation planning

Three small, independent fixes the Phase-3 final review flagged. All in `eval/Nameless.TaskList.Eval`
and `src/Nameless.TaskList.Core/Steps.fs`; no cross-cutting or behaviour-spanning change.

> Note: the originally-considered `Relationships.loadEdges` de-dup was found to be blocked by F# compile
> order — the `get_relationships` tool lives in `Tools.fs`, which compiles BEFORE `Relationships.fs`
> (which depends on `RelationshipEdge` from `Prompts.fs`), so the tool cannot call a shared
> `Relationships` helper without relocating DTOs. Deferred as not-small.

## 1. `Scoring.scoreScalar` guards non-scalar JSON

**Problem.** `scoreScalar` (`eval/Nameless.TaskList.Eval/Scoring.fs`) scores whatever `expected[key]` holds:

```fsharp
match fm.TryGetProperty key with
| true, v ->
    let exp = jsonScalar v   // jsonScalar falls back to GetRawText() for arrays/objects
    let s = if norm exp = norm actual then 1.0 else 0.0
    fields.Add { Field = key; Score = s; Detail = ... }
| _ -> ()
```

If a gold fixture mis-types a scalar field (e.g. `"status": ["pending"]`), `jsonScalar` returns the raw
JSON text `["pending"]` and the field is scored against that — a silent, misleading comparison.

**Fix.** Only treat the field as a scalar assertion when `expected[key]` is a true scalar kind
(`String` / `True` / `False` / `Number`). A non-scalar value under a scalar key is **not a valid scalar
assertion** and is skipped (no `FieldScore` added) — so a malformed fixture can't quietly produce a
0/1. The match becomes:

```fsharp
match fm.TryGetProperty key with
| true, v when (v.ValueKind = JsonValueKind.String
                || v.ValueKind = JsonValueKind.True
                || v.ValueKind = JsonValueKind.False
                || v.ValueKind = JsonValueKind.Number) ->
    let exp = jsonScalar v
    let s = if norm exp = norm actual then 1.0 else 0.0
    fields.Add { Field = key; Score = s; Detail = sprintf "exp=%s act=%s" exp actual }
| _ -> ()
```

(The dataset is controlled and the integrity test guards parse/step/world, so a mis-typed scalar is an
author error caught in review; skipping is the safe, non-misleading behaviour.) `jsonScalar` is unchanged.

## 2. `--baseline` read inside the guarded block

**Problem.** In `eval/Nameless.TaskList.Eval/Program.fs`, the run is wrapped in `try/with` (Ollama
failure → clean exit 2), but the baseline read is **outside** it:

```fsharp
let baseline = opts.Baseline |> Option.map (fun p -> Report.fromJson (File.ReadAllText p))
```

A missing or corrupt `--baseline` path throws here, uncaught, producing a stack trace instead of the
eval's "clear message, exit 2" contract.

**Fix.** Load the baseline inside a guarded block so a bad path prints a clear message and exits 2, e.g.:

```fsharp
let baselineResult =
    match opts.Baseline with
    | None -> Ok None
    | Some p -> try Ok (Some (Report.fromJson (File.ReadAllText p))) with ex -> Error ex.Message
match baselineResult with
| Error e -> eprintfn "could not read --baseline '%s': %s" (Option.defaultValue "" opts.Baseline) e; 2
| Ok baseline ->
    let md = Report.toMarkdown scorecard rs baseline
    …rest of the success path…
```

(Exact placement/shape is the plan's to nail against the current `Program.main` flow; the requirement is:
a bad baseline file → a clear stderr message + exit code 2, never an unhandled stack trace.)

## 3. `Steps.shortlistAndConfirm` → `private`

**Problem.** `Steps.shortlistAndConfirm` is `let` (public). Its only callers are `matchTask`/`matchNote`/
`matchPerson` inside the same `Steps` module; nothing outside `Steps` (pipeline, eval, tests) calls it.

**Fix.** Make it `let private shortlistAndConfirm …`. Pure visibility tightening; no behaviour change.

## 4. Testing

- A `scoreScalar` unit test: an expected `frontmatter` with a non-scalar value under a scalar key
  (e.g. `"status": ["pending"]`) produces **no** `status` field in the result (the field is not scored),
  while a normal scalar still scores as before. (Test via the existing `EvalScoringTests` `genCase`/
  `scoreTask` seam.)
- Existing `EvalScoringTests` / `EvalArgsTests` / `EvalRunnerTests` stay green.
- The `--baseline` and `private` changes are covered by `dotnet build` (the `private` change would fail to
  compile if anything outside `Steps` referenced it) plus the existing eval tests.

## 5. Global constraints

net10.0; behaviour-preserving for all existing eval/Steps tests; deterministic scoring only; writes
through the Verevoir MCP; run tests per-project with `-p:nodeReuse=false -maxcpucount:1` (the host's
solution-wide `dotnet test` CLR-crashes under MSBuild node-reuse).
