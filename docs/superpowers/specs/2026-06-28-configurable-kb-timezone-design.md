# Configurable KB Timezone + Topic Collision Guard — Design

**Date:** 2026-06-28
**Status:** Approved (brainstorm) → ready for implementation planning

Two small vault-correctness fixes, bundled because both touch the write path:
- **A.** Replace the SAST (`+02:00`) timezone offset hardcoded across the codebase with a single
  **configurable** offset (default `+02:00`), used consistently by every timestamp read and write.
- **B.** Add a `freePath` collision guard to `createNewTopic`.

## 1. Problem

**A — timezone.** The KB records timestamps as local wall-clock with an explicit offset (DESIGN §4/§8),
intended to be SAST `+02:00`. But three write sites format with the **server's** offset or UTC, not a
fixed `+02:00`, so output is wrong on a non-SAST host:
- `Pipeline.isoTimestamp` = `ts.ToString("yyyy-MM-ddTHH:mm:sszzz")` — `zzz` renders the *server's* offset.
- `Indexer` `last_updated` = `System.DateTime.UtcNow.ToString("yyyy-MM-dd")` — UTC date (off-by-one near
  midnight off-SAST).
- `Digest` `Generated` = `deps.Today.ToString("yyyy-MM-ddTHH:mm:sszzz")` — server offset again.

Separately, the UTC→local read-shift hardcodes `sastOffset = TimeSpan.FromHours 2.0` in **two** places
(`Adapters.fs`, `Email.fs`). For the KB to be usable outside SAST, the offset must be **one configurable
value** shared by read and write — otherwise timestamps don't round-trip.

**B — topic collisions.** `createNewTopic` writes straight to `topics/active/{slug}.md` with no guard.
Every other entity writer routes through `freePath` (which inserts `-2`, `-3`, … on collision); topics do
not, so two new messages whose titles slug-collide silently overwrite. The embedding fast path uses coarse
`titleFromIntent` titles, raising collision odds.

## 2. Goals / non-goals

**Goals**
- One configurable fixed UTC offset, default `+02:00`, used by every KB timestamp read and write.
- Default value reproduces the current SAST deployment **byte-for-byte**.
- A pure, offset-parameterised `Time` helper (no global mutable state).
- `createNewTopic` never silently overwrites a distinct topic.

**Non-goals**
- DST-aware / IANA-named timezones. The KB already assumes a single fixed offset everywhere; a fixed
  configurable offset covers all non-DST zones (including half-hour offsets like `5.5`). DST handling
  would require per-timestamp `TimeZoneInfo` resolution and is out of scope.
- Re-stamping existing vault files with a new offset (a one-off migration; not part of this change).

## 3. Design — A. Configurable offset

**Config.** New key `Vault:UtcOffsetHours` (double, default `2.0`) in `appsettings.json`. The host binds it
to a `TimeSpan` (`TimeSpan.FromHours`) once at composition and passes that `TimeSpan` to the components
that produce/consume KB timestamps. A missing/blank key defaults to `2.0`.

**`Time` module** — a new module in `KnowledgeBase.fs` (compiles early, before Pipeline/Indexer/Digest),
pure and offset-parameterised:

```fsharp
module Time =
    /// Format a wall-clock DateTime as ISO-8601 with the given fixed offset, independent of the
    /// server's timezone. SpecifyKind Unspecified so the DateTimeOffset ctor never throws on a
    /// Local/Utc-kind input.
    let iso (offset: System.TimeSpan) (ts: System.DateTime) : string =
        System.DateTimeOffset(System.DateTime.SpecifyKind(ts, System.DateTimeKind.Unspecified), offset)
            .ToString("yyyy-MM-ddTHH:mm:sszzz")

    /// The current instant expressed at the given fixed offset.
    let now (offset: System.TimeSpan) : System.DateTimeOffset =
        System.DateTimeOffset.UtcNow.ToOffset(offset)
```

**Threading the configured `TimeSpan`** (default `+02:00` ⇒ identical current behaviour):

- **Pipeline.** `PipelineDeps` gains `UtcOffset: System.TimeSpan`. The module-level `isoTimestamp` and
  `laterIso` become **local closures inside `processMessage`** bound to `deps.UtcOffset`
  (`let isoTimestamp = Time.iso deps.UtcOffset`); the ~15 call sites that use `isoTimestamp ts` are
  unchanged. `laterIso` likewise closes over the offset.
- **Indexer.** Its index-writing entry takes the offset; `last_updated` becomes
  `(Time.now offset).ToString("yyyy-MM-dd")` (the configured-offset date, not UTC).
- **Digest.** `Digest`'s deps record gains the offset; `Generated = Time.iso offset deps.Today`.
- **Adapters / Email read-shift.** The hardcoded `sastOffset` constants in `Adapters.fs` and `Email.fs`
  (the UTC→local conversion on read) are replaced by the same configured offset passed in at construction,
  so a non-default offset round-trips: a message read with offset X is written back with offset X.

**Host wiring.** `Program.fs` reads `Vault:UtcOffsetHours` (default 2.0) → `TimeSpan`, and supplies it to
the `PipelineDeps`, the indexer maintenance task, the digest deps, and the `PostgresMessageSource` /
email components it constructs.

**Behaviour.** With the default `2.0`, output is byte-identical to today on a SAST host (where `zzz`
already yields `+02:00`) and *also* correct on a non-SAST host (previously wrong). With a different
configured offset, all KB timestamps consistently use it.

## 4. Design — B. createNewTopic collision guard

In `Pipeline.createNewTopic`, route the write through the existing `freePath` helper (defined earlier in
the module, in scope):

```fsharp
let path = freePath deps.Vault (Naming.topicPath slug)
deps.Vault.Write(path, MarkdownFile.ToString (Frontmatter.serialize topicRecord) body)
slug, path
```

`freePath` returns `topics/active/{slug}.md` when free, else `…-{n}.md`. The function returns
`(slug, path)`; the caller only uses `path` (the base `slug` is discarded), so no downstream slug/ path
mismatch arises. A genuine reprocess of the same message is already short-circuited earlier by the
message-file idempotency guard, so this only affects two *distinct* new topics that slug-collide — which
previously overwrote and now get a distinct `-2` file.

## 5. Testing

- `Time.iso` emits the given offset (e.g. `+02:00`, `+05:30`) regardless of the input `DateTime.Kind`
  (Unspecified / Local / Utc) — i.e. it never throws and never uses the server offset.
- `Time.now offset` is at the requested offset.
- Pipeline/Indexer/Digest timestamps use the injected offset (a non-default offset in a unit test shows up
  in the written timestamp).
- `createNewTopic` collision: two distinct titles that slugify identically produce two files, the second
  suffixed `-2`.
- Full `dotnet test` stays green at the default offset (existing assertions that pass today must still
  pass; any that asserted a server-derived offset are updated to the explicit configured offset).

## 6. Global constraints

net10.0; default offset `2.0` reproduces current behaviour byte-for-byte; the `Time` helper is pure
(offset passed in, no global mutable state); writes through the Verevoir MCP; no secrets in source.
Run tests per-project with `-p:nodeReuse=false` (the host's solution-wide `dotnet test` CLR-crashes under
MSBuild node-reuse).
