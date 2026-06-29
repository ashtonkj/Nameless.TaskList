# Strip Multi-line Reply Attributions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Email.stripQuotedAndSignature` cut quoted-reply chains introduced by a multi-line `On … wrote:` attribution, not only single-line ones.

**Architecture:** A windowed check inside the existing line scan of one private function in `Email.fs`: an attribution opens on a line matching `^On\b` and is confirmed when a line within the next two lines ends with `wrote:`. Subsumes the current single-line case; nothing else changes.

**Tech Stack:** F# / .NET 10, System.Text.RegularExpressions, xUnit.

## Global Constraints

- Target framework `net10.0`.
- Behaviour-preserving for the four existing `extractText` tests (single-line quoted-chain drop, signature drop, plain-text preference, HTML fallback all unchanged at the default).
- Make edits through the Verevoir MCP `edit_file`/`write_file`. Run tests **per-project** with `-p:nodeReuse=false` (the host's solution-wide `dotnet test` CLR-crashes under MSBuild node-reuse): `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`.

---

## Task 1: Window the attribution match

**Files:**
- Modify: `src/Nameless.TaskList.Core/Email.fs` (`stripQuotedAndSignature`)
- Modify: `tests/Nameless.TaskList.Core.Tests/EmailTests.fs` (add two tests)

**Interfaces:**
- Consumes: nothing new. `stripQuotedAndSignature : string -> string` keeps its signature; only its internals change. `Email.extractText : RawEmail -> string` is unchanged.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Nameless.TaskList.Core.Tests/EmailTests.fs` (after the existing `extractText` tests, before the `isBulk` tests). They use the file's `raw ()` builder + the `{ raw () with TextBody = … }` pattern:

```fsharp
[<Fact>]
let ``extractText drops a multi-line On … wrote: attribution`` () =
    let body = "My answer is yes.\n\nOn Mon, 14 Jun 2026 at 09:00, Dr Naidoo\n<naidoo@example.com> wrote:\n> original question"
    let r = { raw () with TextBody = body }
    let out = Email.extractText r
    Assert.Contains("My answer is yes.", out)
    Assert.DoesNotContain("original question", out)
    Assert.DoesNotContain("naidoo@example.com", out)   // the wrapped attribution line is gone too

[<Fact>]
let ``extractText does not cut a body line that merely starts with On`` () =
    // No line ends with "wrote:", so nothing is an attribution — both lines stay.
    let r = { raw () with TextBody = "On Friday we met; I wrote the notes.\nLet's confirm Tuesday." }
    let out = Email.extractText r
    Assert.Contains("On Friday we met", out)
    Assert.Contains("confirm Tuesday", out)
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EmailTests" -p:nodeReuse=false`
Expected: the new `multi-line` test FAILS — currently `naidoo@example.com` / `original question` leak through because the wrapped attribution isn't matched. (The `does not cut` test passes already; that's fine — it is a guard against the new logic over-cutting.)

- [ ] **Step 3: Window the attribution match in `stripQuotedAndSignature`**

In `src/Nameless.TaskList.Core/Email.fs`, replace the whole `stripQuotedAndSignature` function with:

```fsharp
    /// Strip quoted reply chains and signature blocks from a plain-text body.
    let private stripQuotedAndSignature (text: string) : string =
        if String.IsNullOrWhiteSpace text then ""
        else
            let lines = text.Replace("\r\n", "\n").Split('\n')
            // An attribution opens on a line starting "On" and is confirmed when a line within the
            // next two lines ends with "wrote:" — so a multi-line (wrapped) "On … wrote:" attribution
            // is cut, not just a single-line one. The end-anchored "wrote:" guards against a
            // mid-sentence "I wrote the notes." false cut.
            let opensAttribution = Regex(@"^On\b", RegexOptions.IgnoreCase)
            let endsWrote = Regex(@"wrote:\s*$", RegexOptions.IgnoreCase)
            let isAttribution (i: int) =
                opensAttribution.IsMatch lines.[i]
                && (let last = min (i + 2) (lines.Length - 1)
                    seq { i .. last } |> Seq.exists (fun j -> endsWrote.IsMatch lines.[j]))
            // Cut at a signature delimiter line ("-- ").
            let isSig (l: string) = l.TrimEnd() = "--" || l = "-- "
            let kept = ResizeArray<string>()
            let mutable stop = false
            for i in 0 .. lines.Length - 1 do
                if not stop then
                    let l = lines.[i]
                    if isAttribution i || isSig l then stop <- true
                    elif (l.TrimStart().StartsWith ">") then ()   // drop quoted lines
                    else kept.Add l
            String.Join("\n", kept).Trim()
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EmailTests" -p:nodeReuse=false`
Expected: PASS — the two new tests plus the four existing `extractText` tests (single-line `On Mon, 14 Jun 2026, Dr Naidoo wrote:` still cuts via window offset 0; signature/plain/HTML unchanged).

- [ ] **Step 5: Run the full Core project**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`
Expected: PASS (full Core suite green; only `Email.fs` internals + EmailTests changed).

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Email.fs tests/Nameless.TaskList.Core.Tests/EmailTests.fs
git commit -m "fix: strip multi-line On … wrote: reply attributions in email extractText"
```

---

## Final verification

- [ ] Run `dotnet build -p:nodeReuse=false` — compiles, 0 warnings.
- [ ] Run `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1` — green, including the two new tests.
- [ ] Confirm `git status` clean and the branch holds the one task commit.
