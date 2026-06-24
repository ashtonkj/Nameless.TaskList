namespace Nameless.TaskList.Core

open System.Text.Json
open System.Text.Json.Serialization

module Prompts =

    [<CLIMutable>]
    type Entities =
        { Tasks: string array
          Events: string array
          Commitments: string array
          Notes: string array }

    [<CLIMutable>]
    type Classification =
        { Noise: bool
          NoiseReason: string
          Contexts: string array
          Intent: string
          ActionRequired: bool
          Urgency: string
          PeopleMentioned: string array
          Entities: Entities }

    [<CLIMutable>]
    type TopicMatch =
        { Match: bool
          TopicSlug: string
          Confidence: float
          MatchReason: string
          NewTopicTitle: string }

    let classifySystem = """You are a personal knowledge base assistant processing incoming messages for a busy professional.
Your job is to classify each message and extract structured information from it.

The KB uses the following contexts: family, medical, school, finance, professional, personal-kb.

For each message, respond ONLY with a JSON object in this exact format:

{
  "noise": true/false,
  "noise_reason": "string or null — brief reason if noise",
  "contexts": ["array of matching contexts, or empty"],
  "intent": "string — one sentence describing what the message is about, or null if noise",
  "action_required": true/false,
  "urgency": "critical/high/medium/low/none",
  "people_mentioned": ["array of person names or roles mentioned"],
  "entities": {
    "tasks": ["brief description of any tasks implied"],
    "events": ["brief description of any events mentioned with dates if present"],
    "commitments": ["brief description of any deadlines or obligations mentioned"],
    "notes": ["only DURABLE reference facts worth keeping long-term and across conversations — account/policy/membership numbers, addresses, contact details, medical records, standing preferences. Do NOT create notes for per-message observations, status updates, or anything specific to a single ongoing conversation; those belong to the topic. Empty array if none."]
  }
}

A message is noise if it is:
- A reaction or emoji-only message
- A simple acknowledgement ("ok", "thanks", "👍", "noted")
- Off-topic social chat with no actionable content
- A forwarded joke, meme description, or chain message

If noise is true, all other fields except noise_reason may be null or empty.
Do not add explanation outside the JSON object.
You may call the get_contexts tool to see context definitions before deciding.
You may be given recent conversation history for context. Use it only to disambiguate the meaning of the current message; classify and extract from the "Message to classify" alone, not the history."""

    let topicMatchSystem = """You are a knowledge base assistant. Your job is to decide whether an incoming message
belongs to an existing open topic, or whether it represents a new topic.

You will be given the extracted intent of the new message. You may call the get_topics tool
to list active topics and get_topic to read one in full.

Respond ONLY with a JSON object:

{
  "match": true/false,
  "topic_slug": "slug of matched topic, or null if no match",
  "confidence": 0.0,
  "match_reason": "brief explanation of why this matches or why no match was found",
  "new_topic_title": "if match is false, suggest a concise title for the new topic, else null"
}

Rules:
- Prefer matching an existing topic when the message concerns the same subject, incident, event, person, or thread — including follow-ups, status updates, corrections, and related questions.
- A follow-up about the same incident (e.g. another update on the same gate fault, or a new detail about the same trip) is the SAME topic, even if the wording differs.
- Only create a new topic when the message clearly introduces a distinct subject not covered by any candidate.
- A confidence below 0.6 should result in match: false.
- Do not add explanation outside the JSON object.

Examples:
- A topic "13th Street gate fault" and a message "the gate motor is slow again" -> same topic.
- A topic "Ethan birthday party" and a message about "the party cake order" -> same topic.
- A topic "school fees" and a message about "school sports day" -> different topics."""

    let taskCreateSystem = """You are creating a task entry for a personal knowledge base.
Generate the YAML frontmatter and a brief body for a new task file.

Rules:
- title: short, actionable, starts with a verb (Book, Call, Pay, Send, Review, etc.)
- status: always "pending" for new tasks
- priority: infer from context and urgency — default to "medium" if unsure
- due: include only if a specific date or timeframe was mentioned; use ISO 8601 date; null if none
- context: array — choose from [family, medical, school, finance, professional, personal-kb]
- people: array of person slugs relevant to the task (use [] if none)
- Body: 1–3 sentences of relevant detail.

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""

    let eventCreateSystem = """You are creating an Event entry for a personal knowledge base.
Generate the YAML frontmatter and a brief body for a new event file.

Rules:
- title: short noun phrase naming the occurrence
- when: ISO 8601 datetime. The reference date of the source message is provided; resolve
  relative dates ("next Friday", "the 20th") against it. If only a date is known, use 00:00 and set all_day: true.
- all_day: true when no specific time was given, else false
- context: array — choose from [family, medical, school, finance, professional, personal-kb]
- location: a place name if mentioned, else ""
- people: array of person slugs relevant to the event (use [] if none)
- reminder_days_before: integer, default 3

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""

    let commitmentCreateSystem = """You are creating a Commitment entry for a personal knowledge base.
A commitment is an obligation that exists but does not yet have an assigned task.

Rules:
- title: short noun phrase naming the obligation
- status: always "unresolved" for a new commitment
- priority: infer from context and urgency — default "medium"
- due: ISO 8601 date if a deadline is known, else ""
- context: array — choose from [family, medical, school, finance, professional, personal-kb]
- task_assigned: null
- escalate_after_days: integer, default 7

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""

    let noteCreateSystem = """You are creating a Note entry for a personal knowledge base.
A Note is a DURABLE, evolving reference document for facts that stay useful across many conversations
(e.g. account numbers, contact details, medical records, standing preferences) — not a per-message log.

Rules:
- title: short noun phrase naming the reference subject (e.g. "Medical aid details").
- context: array — choose from [family, medical, school, finance, professional, personal-kb].
- tags: array of short lowercase tags (use [] if none).
- Body: organise the fact under a short markdown section heading; include specifics (numbers, names, dates).

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""

    let personStubSystem = """You are creating a person stub entry for a personal knowledge base.
A new person has been mentioned in a message. Create a minimal person file
based on the available information.

Rules:
- title: the person's canonical full name if known (e.g. "Dr Naidoo", "Sarah Smith"); if no name is known, use their role (e.g. "Ethan's Class Teacher"). Always prefer the canonical name over a nickname or relationship word.
- role: their relationship to the KB owner or their professional role.
- context: choose by the person's ROLE, not the chat it appeared in — one of [family, medical, school, finance, professional]:
    doctor / dentist / specialist / physio / nurse -> medical
    teacher / principal / coach / tutor -> school
    accountant / advisor / banker / broker -> finance
    colleague / manager / client / boss -> professional
    relative / friend / neighbour -> family
  If the role is genuinely unknown, omit context (leave it empty) and the pipeline will fall back.
- aliases: array of other surface forms this person is referred to by (nicknames, relationship words like "Mom", first-name-only). Use [] if none.
- All other unknown fields should be null or omitted.
- Body: 1 sentence describing who this person is and how they relate to the KB owner.
  End with: "⚠ Stub — details to be completed."

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""

    let noteMatchSystem = """You are a knowledge base assistant. Decide whether a new durable fact
belongs to an existing reference note, or whether it is a new note.

You are given the new note's intent and a list of candidate notes (slug, title, summary).
Respond ONLY with a JSON object:

{
  "match": true/false,
  "topic_slug": "slug of the matched note, or null if no match",
  "confidence": 0.0,
  "match_reason": "brief explanation",
  "new_topic_title": "if match is false, a concise title for the new note, else null"
}

Rules:
- Match only if the new fact is about the same subject as an existing note (e.g. another detail of the same account, person, or record).
- A confidence below 0.6 should result in match: false.
- Do not add explanation outside the JSON object."""

    let noteUpdateSystem = """You are updating a durable reference note in a personal knowledge base.
You are given the current note body and a new fact to incorporate.

Rewrite the note body to fold in the new fact. Keep it concise and organised under markdown section
headings. Preserve existing facts; correct them only if the new information supersedes them.

Respond ONLY with the updated markdown body (no frontmatter, no explanation)."""

    let noteUpdateUser (existingBody: string) (intent: string) (raw: string) : string =
        sprintf "Current note body:\n%s\n\nNew fact (intent):\n%s\n\nSource message raw text:\n%s"
            existingBody intent raw

    let topicUpdateSystem = """You are updating a personal knowledge base topic document.
You will be given the current topic document body and a new message that has been linked to it.

Rewrite the document body to reflect the new information. Keep it concise.
Preserve the "## Resolved" section — only add to it, never remove.

Rules:
- Rewrite "## Current understanding" to incorporate the new information naturally
- Update "## Open questions" — remove any the message answers, add new ones it raises
- Add newly resolved items to "## Resolved"
- Do not reference the message itself — just update the facts

Respond ONLY with the updated markdown body (no frontmatter, no explanation).
You may be given recent conversation history for context. Use it to interpret the new message; do not summarise the history itself into the topic body."""

    /// Pull the first JSON object out of a possibly-chatty / fenced model reply.
    let private extractJson (raw: string) : string =
        if isNull raw then "" else
        let start = raw.IndexOf('{')
        let stop = raw.LastIndexOf('}')
        if start >= 0 && stop > start then raw.Substring(start, stop - start + 1)
        else raw

    let private options =
        let o = JsonSerializerOptions(JsonSerializerDefaults.Web)
        o.PropertyNamingPolicy <- JsonNamingPolicy.SnakeCaseLower
        o.NumberHandling <- JsonNumberHandling.AllowReadingFromString
        o

    let private tryParse<'T> (raw: string) : Result<'T, string> =
        try
            let json = extractJson raw
            let value = JsonSerializer.Deserialize<'T>(json, options)
            if obj.ReferenceEquals(value, null) then Error "Model returned null"
            else Ok value
        with ex -> Error(sprintf "Failed to parse model JSON: %s" ex.Message)

    let parseClassification (raw: string) : Result<Classification, string> = tryParse<Classification> raw
    let parseTopicMatch (raw: string) : Result<TopicMatch, string> = tryParse<TopicMatch> raw

    /// Render recent prior messages — as returned by GetRecent (newest-first) — into an
    /// oldest→newest transcript for use as conversation context. Media-only turns (empty
    /// content) render with a [type] placeholder so the model knows a non-text turn occurred.
    let renderHistory (recent: ChatMessage list) : string =
        recent
        |> List.rev
        |> List.map (fun m ->
            let body =
                if not (System.String.IsNullOrWhiteSpace m.Content) then m.Content.Trim()
                else
                    match (if isNull m.MediaType then "" else m.MediaType).ToLowerInvariant() with
                    | "image"    -> "[image]"
                    | "audio"    -> "[voice note]"
                    | "video"    -> "[video]"
                    | "document" -> "[document]"
                    | _          -> "[no text]"
            let sender = if isNull m.SenderName then "Unknown" else m.SenderName
            sprintf "%s: %s" sender body)
        |> String.concat "\n"

    /// Build the classify user-message: the current message, optionally preceded by a
    /// conversation-history block. Empty history passes the content through unchanged so
    /// no-history processing (and existing tests) is byte-for-byte identical to before.
    let classifyUser (history: string) (content: string) : string =
        if System.String.IsNullOrWhiteSpace history then content
        else
            sprintf "Recent conversation (oldest to newest, for context only):\n%s\n\n---\nMessage to classify:\n%s"
                history content

    /// Build the topic-update user-message. With empty history the payload is byte-for-byte
    /// identical to the pre-history-feature format, so no-history processing is unchanged.
    let topicUpdateUser (history: string) (existingBody: string) (content: string) (intent: string) : string =
        if System.String.IsNullOrWhiteSpace history then
            sprintf "Current topic body:\n%s\n\nNew message raw text:\n%s\n\nExtracted intent:\n%s"
                existingBody content intent
        else
            sprintf "Current topic body:\n%s\n\nRecent conversation (oldest to newest, for context):\n%s\n\nNew message raw text:\n%s\n\nExtracted intent:\n%s"
                existingBody history content intent

    let dailyBriefingSystem = """You are generating a daily briefing for a personal knowledge base.
Be concise. The owner is a busy professional — surface only what matters today and this week.

Structure your response as plain text (not markdown headers) in this order:
1. Today's priorities — top 3 tasks, one line each
2. Upcoming in 7 days — events and task deadlines
3. Needs attention — commitments without tasks, stale topics
4. Optional: one low-priority item worth doing if time allows

Keep the whole briefing under 200 words.
Use plain language — this may be delivered as a WhatsApp message."""

    let weeklyDigestSystem = """You are generating a weekly digest for a personal knowledge base.
Be concise. The owner is a busy professional — surface what matters for the week ahead.

Structure your response as plain text (not markdown headers) in this order:
1. Top priorities — the highest-scored tasks, one line each
2. Upcoming in 14 days — events and task deadlines
3. Needs attention — commitments without tasks, stale topics needing review
4. Optional: one item worth getting ahead on

Keep the whole digest under 300 words.
Use plain language — this may be delivered as a WhatsApp message."""
