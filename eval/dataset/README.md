# Eval gold dataset

Hand-labelled cases for the prompt-eval CLI (`eval/Nameless.TaskList.Eval`). Run with:

    dotnet run --project eval/Nameless.TaskList.Eval -- --model <model> --report out.md

## Layout

- `classify/*.json`, `topic-match/*.json` — one gold case per file. `step` selects the scorer;
  `world` names the vault fixture seeded for the case's tool calls.
- `_worlds/_base/` — generic, non-PII context definitions + standing identities, always seeded.
- `_worlds/<name>/` — a case's anonymised people / topics, overlaid on `_base` (named files win).

## Case schema

```json
{ "id": "...", "step": "classify|topic-match", "tags": ["..."], "world": "<world or _base>",
  "input": { "message": "...", "intent": "...", "referenceDate": "YYYY-MM-DD", "history": [] },
  "expected": { ... step-specific gold ... } }
```

- **classify** expected: `noise` (bool), `contexts`/`tasks`/`events`/`people` (arrays; entries may be
  `*globs*` or substrings). Only the fields present are scored.
- **topic-match** expected: `{ "decision": "match", "slug": "..." }` or
  `{ "decision": "create", "titleContains": ["..."] }`.
- **task-create / event-create / commitment-create / note-create** `input`: `message`, `intent`,
  `referenceDate`, `contexts` (array), `urgency`. `expected`: a `frontmatter` object of model-generated
  fields to assert (`status`/`priority`/`context`/`due` for tasks; `all_day`/`when`/`location`/
  `reminder_days_before`/`context` for events; `status`/`priority`/`due`/`task_assigned`/
  `escalate_after_days`/`context` for commitments; `context`/`tags` for notes), plus optional
  `titleMatches` (regex) and `bodyContains` (substrings). Linkage fields
  (topic/source_message/tasks_linked/people) are never asserted.

- **task-match / note-match / person-match** `input`: `intent` (task/note) or `name` + `contexts`
  (person); the case's `world` seeds the candidate entities/people. `expected`:
  `{"decision":"match","slug":"…"}` or `{"decision":"nomatch"}`. Needs the live embedder.
- **task-update / note-update** `input`: `existingFile` (task) or `existingBody` (note) + `intent` +
  `message`. `expected`: task = `frontmatter`/`titleMatches`/`bodyContains`; note = `bodyContains` (body-only).
- **person-stub-create** `input`: `name` + `contexts`. `expected`: `frontmatter` (`role`/`context`/
  `aliases`) + `titleMatches`/`bodyContains`.
- **relationship-extract** `input`: `slugs` (co-mentioned resolved slugs) + `message`. `expected`:
  `{"relationships":[{"from","to","relation"}, …]}`, scored as an edge-set F1 (directed relations keep
  from→to; symmetric relations are order-insensitive; descriptor/confidence ignored).

## Anonymisation rule (privacy-first)

Seed cases from real pipeline examples, but before committing apply ONE name/number/address map to
BOTH the message text AND the case's world files, so a person the message names exists as the same
anonymised `people/…` file the tools surface, and `expected` uses the same fictional slugs. Never
commit a real name, phone number, account number, or address.
