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
    "notes": ["any factual information worth storing"]
  }
}

A message is noise if it is:
- A reaction or emoji-only message
- A simple acknowledgement ("ok", "thanks", "👍", "noted")
- Off-topic social chat with no actionable content
- A forwarded joke, meme description, or chain message

If noise is true, all other fields except noise_reason may be null or empty.
Do not add explanation outside the JSON object.
You may call the get_contexts tool to see context definitions before deciding."""

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
- Only match if the new message is clearly about the same subject as an existing topic
- A confidence below 0.75 should result in match: false
- Prefer creating a new topic over forcing a weak match
- Do not add explanation outside the JSON object."""

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
A note captures a fact or piece of reference information worth keeping.

Rules:
- title: short noun phrase naming the fact
- context: array — choose from [family, medical, school, finance, professional, personal-kb]
- tags: array of short lowercase tags (use [] if none)
- Body: 1–3 sentences capturing the fact, including any specifics (numbers, names, dates).

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""

    let personStubSystem = """You are creating a person stub entry for a personal knowledge base.
A new person has been mentioned in a message. Create a minimal person file
based on the available information.

Rules:
- title: full name if known, role if name not known (e.g. "Ethan's Class Teacher")
- role: their relationship to the KB owner or their professional role
- context: infer from the message context — choose from [family, medical, school, finance, professional]
- All unknown fields should be null or omitted
- Body: 1 sentence describing who this person is and how they relate to the KB owner.
  End with: "⚠ Stub — details to be completed."

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""

    let topicUpdateSystem = """You are updating a personal knowledge base topic document.
You will be given the current topic document body and a new message that has been linked to it.

Rewrite the document body to reflect the new information. Keep it concise.
Preserve the "## Resolved" section — only add to it, never remove.

Rules:
- Rewrite "## Current understanding" to incorporate the new information naturally
- Update "## Open questions" — remove any the message answers, add new ones it raises
- Add newly resolved items to "## Resolved"
- Do not reference the message itself — just update the facts

Respond ONLY with the updated markdown body (no frontmatter, no explanation)."""

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
