# Strip Multi-line Reply Attributions — Design

**Date:** 2026-06-29
**Status:** Approved (brainstorm) → ready for implementation planning

A small fix to `Email.stripQuotedAndSignature` (in `src/Nameless.TaskList.Core/Email.fs`) so that
quoted-reply chains introduced by a **multi-line** `On … wrote:` attribution are stripped, not just
single-line ones.

## 1. Problem

`stripQuotedAndSignature` cuts the body at the first line matching `^On .+ wrote:\s*$` — `On` and
`wrote:` must be on the **same** line. Real mail clients wrap the attribution across lines, e.g.:

```
My answer is yes.

On Mon, 14 Jun 2026 at 09:00, Dr Naidoo
<naidoo@example.com> wrote:
> the original question
```

Neither wrapped line matches the single-line regex, so the cut never fires. Only `>`-prefixed lines are
dropped; the attribution prose (and any non-`>` quoted continuation) leaks into the text fed to the
classifier, polluting extraction.

## 2. Change

Replace the single-line attribution match with a **windowed** check inside the existing line scan:

- An attribution **opens** on a line matching `^On\b` (case-insensitive).
- It is confirmed when a line within the window `[i, i+2]` (this line or the next two) ends with `wrote:`
  (`wrote:\s*$`, case-insensitive).
- When both hold, the `On` line `i` is the cut point: stop keeping lines there (drop it and everything
  after).

Properties:
- **Subsumes the single-line case:** `On X wrote:` both starts with `On` and ends with `wrote:` on the
  same line (window offset 0), so the existing behaviour — and its test — is preserved.
- **Catches 2–3-line attributions** (the common Gmail/Outlook wraps).
- **Guarded against false cuts:** the terminator is end-anchored (`wrote:$`), so a mid-sentence
  occurrence like "On Friday we met; I wrote the notes." does not trigger (it neither needs nor has a
  line ending in `wrote:` within the window).

Everything else in `stripQuotedAndSignature` is unchanged: the `--` signature cut, and dropping
`>`-prefixed quoted lines. The change is confined to one private function; `extractText`/`toChatMessage`
signatures and all other behaviour are untouched.

## 3. Implementation sketch

In the line loop, replace the `attribution.IsMatch l` condition with a helper that, given the current
index `i`, returns true when `lines.[i]` matches `^On\b` and some `lines.[j]` for `j` in
`[i, min(i+2, last)]` matches `wrote:\s*$`. Both regexes `IgnoreCase`. The signature (`isSig`) and quoted
(`>`) handling stay as they are; the `else keep` branch is unchanged.

## 4. Testing

Keep all four existing `extractText` tests passing (plain-text preference, HTML fallback, single-line
quoted-chain drop, signature drop). Add:

- **Multi-line attribution dropped:** body `"My answer is yes.\n\nOn Mon, 14 Jun 2026 at 09:00, Dr Naidoo\n<naidoo@example.com> wrote:\n> original"` → `extractText` returns `"My answer is yes."` (attribution + quote gone).
- **No false cut:** body `"On Friday we met; I wrote the notes.\nLet's confirm Tuesday."` → both lines are
  kept (no line ends with `wrote:`), so nothing is stripped.

## 5. Global constraints

net10.0; behaviour-preserving for the existing four `extractText` tests (default single-line + signature +
HTML behaviour unchanged); writes through the Verevoir MCP; run tests per-project with
`-p:nodeReuse=false` (the host's solution-wide `dotnet test` CLR-crashes under MSBuild node-reuse).
