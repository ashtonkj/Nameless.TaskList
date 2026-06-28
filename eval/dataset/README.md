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

## Anonymisation rule (privacy-first)

Seed cases from real pipeline examples, but before committing apply ONE name/number/address map to
BOTH the message text AND the case's world files, so a person the message names exists as the same
anonymised `people/…` file the tools surface, and `expected` uses the same fictional slugs. Never
commit a real name, phone number, account number, or address.
