# Eval / Steps Robustness Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Three small reviewer-flagged robustness fixes — `Scoring.scoreScalar` ignores a non-scalar expected value, the eval `--baseline` read is guarded (clean exit, not a stack trace), and `Steps.shortlistAndConfirm` is made `private`.

**Architecture:** All three are local edits in `eval/Nameless.TaskList.Eval` (Scoring.fs, Program.fs) and `src/Nameless.TaskList.Core/Steps.fs`, with one new `EvalScoringTests` unit test. No cross-cutting change.

**Tech Stack:** F# / .NET 10, System.Text.Json, xUnit.

## Global Constraints

- Target framework `net10.0`; behaviour-preserving for all existing eval/Steps tests.
- Deterministic scoring only.
- Make edits through the Verevoir MCP `edit_file`/`write_file`. Run tests **per-project** with `-p:nodeReuse=false` (the host's solution-wide `dotnet test` CLR-crashes under MSBuild node-reuse): `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`.

---

## Task 1: The three polish fixes

**Files:**
- Modify: `eval/Nameless.TaskList.Eval/Scoring.fs` (`scoreScalar`)
- Modify: `eval/Nameless.TaskList.Eval/Program.fs` (`main`, the `Ok rs ->` branch)
- Modify: `src/Nameless.TaskList.Core/Steps.fs` (`shortlistAndConfirm` visibility)
- Modify: `tests/Nameless.TaskList.Core.Tests/EvalScoringTests.fs` (add a `scoreScalar` test)

**Interfaces:**
- Consumes: the existing `EvalScoringTests` helpers `genCase : string -> string -> Dataset.Case` and `taskOutcome : status -> priority -> due -> title -> body -> Steps.EntityOutcome<Task>`; `Scoring.scoreTask`.
- Produces: no new public surface. `Scoring.scoreScalar` keeps its signature; `Steps.shortlistAndConfirm` becomes `private`.

- [ ] **Step 1: Write the failing `scoreScalar` test**

Append to `tests/Nameless.TaskList.Core.Tests/EvalScoringTests.fs` (it already has `genCase` and `taskOutcome`):

```fsharp
[<Fact>]
let ``scoreScalar ignores a non-scalar expected value`` () =
    // A mis-typed gold fixture: "status" is an array under a scalar key. It must NOT be scored
    // (no "status" field in the result), while a genuine scalar ("priority") still scores.
    let case = genCase "task-create" """{"frontmatter":{"status":["pending"],"priority":"medium"}}"""
    let r = Scoring.scoreTask case (Ok (taskOutcome "pending" "medium" "" "Do it" ""))
    Assert.False(r.Fields |> List.exists (fun f -> f.Field = "status"))
    Assert.True(r.Fields |> List.exists (fun f -> f.Field = "priority"))
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalScoringTests" -p:nodeReuse=false`
Expected: the new test FAILS — currently `scoreScalar` scores the non-scalar `status` via `jsonScalar`'s `GetRawText()` fallback, so a `status` field (score 0) is present and the `Assert.False` fails.

- [ ] **Step 3: Guard `scoreScalar` to scalar kinds**

In `eval/Nameless.TaskList.Eval/Scoring.fs`, replace the `scoreScalar` body's match. Change:

```fsharp
    let private scoreScalar (fm: JsonElement) (key: string) (actual: string) (fields: ResizeArray<FieldScore>) =
        match fm.TryGetProperty key with
        | true, v ->
            let exp = jsonScalar v
            let s = if norm exp = norm actual then 1.0 else 0.0
            fields.Add { Field = key; Score = s; Detail = sprintf "exp=%s act=%s" exp actual }
        | _ -> ()
```

to:

```fsharp
    let private scoreScalar (fm: JsonElement) (key: string) (actual: string) (fields: ResizeArray<FieldScore>) =
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

(`jsonScalar` is unchanged. A non-scalar value under a scalar key now falls through the `| _ -> ()` arm — the field is not scored.)

- [ ] **Step 4: Guard the `--baseline` read in `Program.main`**

In `eval/Nameless.TaskList.Eval/Program.fs`, replace the `| Ok rs ->` branch (currently lines ~42-56) so the baseline read is wrapped — a bad `--baseline` path prints a clear message and returns exit code 2 instead of throwing. Replace:

```fsharp
            | Ok rs ->
                let scorecard = Report.aggregate model rs
                let baseline = opts.Baseline |> Option.map (fun p -> Report.fromJson (File.ReadAllText p))
                let md = Report.toMarkdown scorecard rs baseline
                printfn "%s" md
                match opts.Report with
                | Some path ->
                    File.WriteAllText(path, md)
                    File.WriteAllText(Path.ChangeExtension(path, ".json"), Report.toJson scorecard)
                | None -> ()
                if scorecard.Overall < opts.Threshold then
                    eprintfn "FAIL: overall %.3f < threshold %.3f" scorecard.Overall opts.Threshold
                    1
                else
                    0
```

with:

```fsharp
            | Ok rs ->
                let scorecard = Report.aggregate model rs
                let baselineResult =
                    match opts.Baseline with
                    | None -> Ok None
                    | Some p -> try Ok (Some (Report.fromJson (File.ReadAllText p))) with ex -> Error ex.Message
                match baselineResult with
                | Error e ->
                    eprintfn "could not read --baseline '%s': %s" (Option.defaultValue "" opts.Baseline) e
                    2
                | Ok baseline ->
                    let md = Report.toMarkdown scorecard rs baseline
                    printfn "%s" md
                    match opts.Report with
                    | Some path ->
                        File.WriteAllText(path, md)
                        File.WriteAllText(Path.ChangeExtension(path, ".json"), Report.toJson scorecard)
                    | None -> ()
                    if scorecard.Overall < opts.Threshold then
                        eprintfn "FAIL: overall %.3f < threshold %.3f" scorecard.Overall opts.Threshold
                        1
                    else
                        0
```

(The success path is byte-for-byte the same, only nested under the `Ok baseline` arm; the only new behaviour is the guarded read.)

- [ ] **Step 5: Make `Steps.shortlistAndConfirm` private**

In `src/Nameless.TaskList.Core/Steps.fs`, change the binding (currently `let shortlistAndConfirm`) to `let private shortlistAndConfirm`. Its only callers — `matchTask`/`matchNote`/`matchPerson` — are in the same module, so this compiles. (If `dotnet build` reports an external reference, stop and report; the spec asserts there is none.)

- [ ] **Step 6: Run the tests + build**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalScoringTests" -p:nodeReuse=false`
Expected: PASS — the new test passes (no `status` field for the non-scalar case); existing EvalScoringTests still pass.

Run: `dotnet build -p:nodeReuse=false`
Expected: Build succeeds, 0 warnings (the `private` change compiles; the host eval Program compiles with the guarded branch).

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`
Expected: PASS — full Core project green (EvalScoringTests + EvalArgsTests + EvalRunnerTests + Steps/match tests all pass; behaviour-preserving).

- [ ] **Step 7: Commit**

```bash
git add eval/Nameless.TaskList.Eval/Scoring.fs eval/Nameless.TaskList.Eval/Program.fs \
        src/Nameless.TaskList.Core/Steps.fs tests/Nameless.TaskList.Core.Tests/EvalScoringTests.fs
git commit -m "fix: scoreScalar ignores non-scalar JSON, guard --baseline read, private shortlistAndConfirm"
```

---

## Final verification

- [ ] Run `dotnet build -p:nodeReuse=false` — compiles, 0 warnings.
- [ ] Run `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1` — green, including the new `scoreScalar` test.
- [ ] Confirm `git status` clean and the branch holds the one task commit.
