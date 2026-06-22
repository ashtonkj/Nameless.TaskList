# Personal Knowledge Base — Design Document

> **Version:** 1.0  
> **Date:** 2026-06-17  
> **Stack:** Obsidian (vault) · n8n (automation) · Ollama (local LLM) · self-hosted  
> **Format:** Open Knowledge Format (OKF) — markdown files with YAML frontmatter  

---

## 1. Overview

This KB is a self-hosted, privacy-first personal knowledge base designed to:

- Ingest messages from multiple sources (WhatsApp direct, WhatsApp groups, email)
- Extract tasks, events, commitments, and relationships from those messages
- Track and prioritise actionable items across family, medical, school, finance, and professional contexts
- Provide an LLM-queryable graph of people, places, and ongoing topics

All processing happens locally via n8n and Ollama. No data leaves the local network.

---

## 2. Core Design Principles

1. **Files are the database.** Every concept is a markdown file with YAML frontmatter. The graph lives in the links between files.
2. **Channels are persistent; messages are atomic.** A WhatsApp chat is never a "thread" — it is a long-running channel. Individual messages are ingested one at a time as they arrive.
3. **Topics accumulate meaning over time.** A conversation about birthday planning may span weeks. The `Topic` concept gathers related messages under a single evolving document rather than cutting by date.
4. **Tasks are spawned, not written by hand.** Tasks should trace back to a source message and, where applicable, a topic.
5. **The pipeline is idempotent per message.** Each message gets one file. Processing the same message twice should be a no-op.
6. **Meta lives in the vault.** Reminder rules, priority weights, and pipeline instructions are markdown files — editable in Obsidian, readable by the pipeline.

---

## 3. Directory Structure

```
personal-kb/
├── people/
│   ├── index.md
│   ├── family/
│   │   ├── wife.md
│   │   ├── ethan.md
│   │   └── zoe.md
│   ├── medical/
│   │   ├── dr-naidoo.md
│   │   └── dr-van-der-berg.md
│   ├── school/
│   │   ├── ms-pretorius.md
│   │   └── mr-jacobs.md
│   └── professional/
│       ├── index.md
│       └── alice-du-plessis.md
│
├── tasks/
│   ├── index.md
│   ├── pending/
│   │   ├── book-ethan-flu-vaccine.md
│   │   ├── pay-school-fees-july.md
│   │   └── renew-pilots-licence.md
│   ├── in-progress/
│   │   └── setup-n8n-whatsapp-pipeline.md
│   └── done/
│       └── 2026-06/
│           └── schedule-zoe-dentist.md
│
├── events/
│   ├── index.md
│   └── 2026/
│       ├── 06/
│       │   ├── ethan-sports-day-2026-06-20.md
│       │   └── gp-checkup-family-2026-06-25.md
│       └── 07/
│           ├── ethan-birthday-party-2026-07-19.md
│           └── school-term-3-starts-2026-07-14.md
│
├── channels/
│   ├── index.md
│   ├── whatsapp/
│   │   ├── wife-direct.md
│   │   ├── school-parents-group.md
│   │   └── body-corporate-group.md
│   └── email/
│       ├── dr-naidoo-practice.md
│       └── ethan-school-admin.md
│
├── messages/
│   ├── whatsapp-wife-direct/
│   │   ├── 2026-06-15T08-32-11.md
│   │   └── 2026-06-15T14-17-45.md
│   ├── whatsapp-school-parents-group/
│   │   └── 2026-06-14T07-15-03.md
│   └── email-dr-naidoo-practice/
│       └── 2026-06-10T11-04-00.md
│
├── topics/
│   ├── index.md
│   ├── active/
│   │   ├── ethan-birthday-party-2026.md
│   │   ├── car-service-overdue.md
│   │   └── school-fees-july.md
│   └── resolved/
│       └── zoe-dentist-booking.md
│
├── contexts/
│   ├── family.md
│   ├── medical.md
│   ├── school.md
│   ├── finance.md
│   └── professional.md
│
├── commitments/
│   ├── index.md
│   ├── school-fees-q3-2026.md
│   └── car-service-overdue.md
│
├── locations/
│   ├── dr-naidoo-rooms.md
│   ├── ethan-school.md
│   └── faor.md
│
├── notes/
│   ├── index.md
│   ├── ethan-allergies.md
│   └── recommended-electrician.md
│
├── projects/
│   ├── ethan-school-admission-2027.md
│   └── personal-kb-setup.md
│
└── _meta/
    ├── AGENTS.md
    ├── reminder-rules.md
    ├── priority-weights.md
    └── pipeline-log.md
```

---

## 4. Concept Types & Frontmatter Schemas

### 4.1 Person

One file per person. Subdirectory reflects their primary relationship context.

```yaml
---
type: Person
title: Dr. Priya Naidoo
role: Paediatrician
context: [medical, family]
location: locations/dr-naidoo-rooms.md
people_linked: [ethan]
channel: channels/email/dr-naidoo-practice.md
phone: "+27 11 xxx xxxx"
email: "practice@example.com"
tags: [doctor, paeds, sandton]
---

Ethan's paediatrician since 2022.
Referral required for specialist appointments.
Accepts Discovery Health (plan: Comprehensive).
Rooms in Sandton — allow 30min travel from home.
```

**Key fields:**
- `context` — which domain(s) this person belongs to
- `people_linked` — family members this person is associated with
- `channel` — preferred/known communication channel
- `location` — primary physical location

---

### 4.2 Task

Actionable item with a responsible owner (always you) and a traceable source.

```yaml
---
type: Task
title: Book Ethan's flu vaccine
status: pending
priority: high
due: 2026-06-30
context: [medical, family]
people: [ethan, dr-naidoo]
topic: topics/active/ethan-flu-vaccine.md
source_message: messages/email-dr-naidoo-practice/2026-06-10T11-04-00.md
project: null
commitment: null
---

Ethan needs his annual flu vaccine before school term 3.
Dr Naidoo's rooms confirmed availability this month.
Call +27 11 xxx xxxx to book — reference Discovery Health membership.
```

**Status values:** `pending` · `in-progress` · `blocked` · `done` · `cancelled`  
**Priority values:** `critical` · `high` · `medium` · `low`

---

### 4.3 Event

Time-bound occurrence. Date-pathed in directory for natural ordering.

```yaml
---
type: Event
title: Ethan's Birthday Party
when: 2026-07-19T14:00:00+02:00
duration_hours: 3
all_day: false
context: [family]
location: locations/acrobranch-northgate.md
people: [ethan, wife, ms-pretorius]
topic: topics/active/ethan-birthday-party-2026.md
tasks_linked:
  - tasks/pending/book-venue-ethan-birthday.md
  - tasks/pending/order-birthday-cake.md
reminder_days_before: 3
---

Ethan's 8th birthday party at Acrobranch Northgate.
~12 kids attending. Cake to be collected morning of event.
Parking: main lot off Northgate Drive.
```

---

### 4.4 Channel

Persistent communication surface. Never expires. Updated by pipeline on each processed message.

```yaml
---
type: Channel
title: Wife (Direct)
platform: whatsapp-direct
context: [family]
people: [wife]
signal_weight: high
last_processed: 2026-06-17T09:05:22Z
message_count_total: 4821
active_topics:
  - topics/active/ethan-birthday-party-2026.md
  - topics/active/car-service-overdue.md
---

Primary family coordination channel.
High signal — most messages actionable.
Process immediately on receipt.
```

**Platform values:** `whatsapp-direct` · `whatsapp-group` · `email` · `sms`  
**Signal weight values:** `high` · `medium` · `low`

```yaml
---
type: Channel
title: School Parents Group
platform: whatsapp-group
context: [school, family]
people: [ms-pretorius, wife]
members_unknown: 34
signal_weight: low
last_processed: 2026-06-14T07-22-00Z
message_count_total: 1204
active_topics:
  - topics/active/school-fees-july.md
---

School parents WhatsApp group.
Low signal — many social messages, occasional actionable items.
Typical ratio: 1 actionable per 40–50 messages.
```

---

### 4.5 Message

Atomic ingestion record. One file per received message. Subdirectory mirrors channel slug.

```yaml
---
type: Message
channel: channels/whatsapp/wife-direct.md
timestamp: 2026-06-15T14:17:45+02:00
sender: wife
noise: false
topic: topics/active/ethan-birthday-party-2026.md
spawned_tasks:
  - tasks/pending/book-venue-ethan-birthday.md
spawned_events: []
spawned_notes: []
processed_by: ollama/llama3.2
---

## Raw
"Can you call Acrobranch tomorrow and check if they have availability on the 19th?"

## Extracted intent
Action required: call venue to confirm availability for 19 July birthday party.
```

**Noise messages** (reactions, acknowledgements, off-topic chat) still get a file but are minimal:

```yaml
---
type: Message
channel: channels/whatsapp/wife-direct.md
timestamp: 2026-06-17T09:05:22+02:00
sender: wife
noise: true
topic: null
processed_by: ollama/llama3.2
---
```

---

### 4.6 Topic

Semantic conversation unit. Accumulates meaning across many messages over days or weeks. Rewritten by LLM on each relevant new message.

```yaml
---
type: Topic
title: Ethan's birthday party planning
status: active
context: [family]
channel: channels/whatsapp/wife-direct.md
people: [wife, ethan]
first_seen: 2026-06-10T19:44:00+02:00
last_updated: 2026-06-17T09:05:22+02:00
spawned_tasks:
  - tasks/pending/book-venue-ethan-birthday.md
  - tasks/pending/order-birthday-cake.md
spawned_events:
  - events/2026/07/ethan-birthday-party-2026-07-19.md
message_refs:
  - messages/whatsapp-wife-direct/2026-06-10T19-44-00.md
  - messages/whatsapp-wife-direct/2026-06-15T08-32-11.md
  - messages/whatsapp-wife-direct/2026-06-15T14-17-45.md
---

## Current understanding
Party planned for 19 July at Acrobranch Northgate.
~12 kids. Invitations sent. Cake not yet ordered.
Catering/snacks decision outstanding.

## Open questions
- Cake: not yet ordered — decision needed this week
- Catering: bring own snacks or order from venue?

## Resolved
- Venue confirmed: Acrobranch Northgate (was shortlisted with Jump Zone)
- Invitations sent to class
```

**Status values:** `active` · `resolved` · `spawned` · `stale`

Topic lifecycle:
```
active → resolved   (conversation concluded, outcome known)
active → spawned    (produced a task/event that now owns it)
active → stale      (no new messages in 14+ days — flag for review)
```

---

### 4.7 Context

Domain definition used for priority weighting and grouping. Referenced by most other concept types.

#### `contexts/family.md`
```yaml
---
type: Context
title: Family
priority_weight: high
deadline_sensitive: false
escalation_threshold: 2
tags: [family, home, personal]
people_linked:
  - people/family/wife.md
  - people/family/ethan.md
  - people/family/zoe.md
sub_contexts:
  - contexts/medical.md
  - contexts/school.md
channels_linked:
  - channels/whatsapp/wife-direct.md
  - channels/whatsapp/body-corporate-group.md
reminder_rule: "_meta/reminder-rules.md#family"
---

Catch-all for home and family life not covered by
medical, school, or finance sub-contexts.
Includes: home maintenance, family admin, social events,
holiday planning, vehicle management.

## Priority guidance
Tasks in this context without a hard deadline default to medium.
Tasks affecting multiple family members escalate to high.
```

#### `contexts/medical.md`
```yaml
---
type: Context
title: Medical
parent_context: contexts/family.md
priority_weight: high
deadline_sensitive: true
escalation_threshold: 1
tags: [health, appointments, prescriptions, medical-records]
people_linked:
  - people/medical/dr-naidoo.md
  - people/medical/dr-van-der-berg.md
channels_linked:
  - channels/email/dr-naidoo-practice.md
reminder_rule: "_meta/reminder-rules.md#medical"
---

Covers all health-related tasks, appointments, and records
for all family members.

## Priority guidance
Any overdue prescription or lapsed immunisation: critical.
Upcoming appointment within 3 days: high (auto-escalated by reminder rule).
Referral or specialist booking: high — tends to have long lead times.
Routine annual checkup scheduling: medium.

## Insurance
Medical aid: Discovery Health — Comprehensive Plan
Member number: stored in notes/medical-aid-details.md
```

#### `contexts/school.md`
```yaml
---
type: Context
title: School
parent_context: contexts/family.md
priority_weight: high
deadline_sensitive: true
escalation_threshold: 1
tags: [school, education, fees, events]
people_linked:
  - people/school/ms-pretorius.md
  - people/school/mr-jacobs.md
channels_linked:
  - channels/whatsapp/school-parents-group.md
  - channels/email/ethan-school-admin.md
reminder_rule: "_meta/reminder-rules.md#school"
---

Covers all school-related tasks, fees, events, and communications
for Ethan (and Zoe when enrolled).

## Priority guidance
Fee payment deadlines: critical (financial penalty if missed).
Permission slips and forms: high — typically 1-week turnaround.
School events (sports days, concerts): medium — calendar only, prep task if needed.
General group chat items: low — review in weekly digest.

## Key dates pattern
Term dates, fee schedules, and exam timetables go in events/ when known.
```

#### `contexts/finance.md`
```yaml
---
type: Context
title: Finance
priority_weight: high
deadline_sensitive: true
escalation_threshold: 1
tags: [finance, payments, tax, insurance, investments]
channels_linked: []
reminder_rule: "_meta/reminder-rules.md#finance"
---

Covers all financial obligations: recurring payments, tax deadlines,
insurance renewals, and investment reviews.

## Priority guidance
SARS deadlines: critical — no exceptions.
Recurring payment failures or missed debit orders: critical.
Insurance renewal within 30 days: high.
General financial review tasks: medium.

## Recurring obligations (reference)
- Medical aid: monthly debit — 1st of month
- School fees: quarterly — due 1st of term month
- Home insurance: annual renewal — check notes/insurance-renewals.md
```

#### `contexts/professional.md`
```yaml
---
type: Context
title: Professional
priority_weight: medium
deadline_sensitive: true
escalation_threshold: 2
tags: [work, career, clients, projects]
channels_linked:
  - channels/email/work-inbox.md
reminder_rule: "_meta/reminder-rules.md#professional"
---

Covers work tasks, client communications, and career obligations.
Lower default priority than family contexts — work has its own
tracking system; this KB captures spillover and personal career items.

## Priority guidance
Client-facing deliverables with hard deadlines: high.
Internal meetings and follow-ups: medium.
Career development tasks (certifications, CPD): medium — escalate if deadline approaching.
Flying licence renewals and medicals: high — regulatory, non-negotiable.
```

---

### 4.8 Commitment

An obligation that exists but does not yet have an assigned task. The bridge between "something needs to be done" and "I've decided what to do."

```yaml
---
type: Commitment
title: Q3 school fees payment
status: unresolved
priority: high
due: 2026-07-01
context: [finance, school]
topic: topics/active/school-fees-july.md
task_assigned: null
escalate_after_days: 7
source_message: messages/whatsapp-school-parents-group/2026-06-14T07-15-03.md
---

Term 3 fees due by 1 July. No task yet assigned.
Amount: stored in notes/school-fee-schedule-2026.md.
Payment method: EFT — school banking details in notes/.

Escalate to priority: critical if no task assigned by 24 June.
```

---

### 4.9 Location

Physical place referenced by people, events, or tasks.

```yaml
---
type: Location
title: Dr. Naidoo's Rooms
address: "Suite 4, Sandton Medi-Centre, Rivonia Rd, Sandton, 2196"
coordinates: [-26.1077, 28.0567]
context: [medical]
people_linked: [dr-naidoo]
parking: "Basement P2 — first 30 min free, validate at reception"
travel_time_from_home_min: 25
tags: [medical, sandton, consulting-rooms]
---

Rooms in Sandton Medi-Centre.
Reception: +27 11 xxx xxxx.
Book at least 2 weeks in advance for non-urgent appointments.
Lift access for pram/wheelchair — use south entrance.
```

---

### 4.10 Note

Freeform catch-all for facts, observations, and reference material that don't fit elsewhere. Can link to any other concept.

```yaml
---
type: Note
title: Ethan's allergies & dietary notes
context: [medical, family]
people_linked: [ethan, dr-naidoo]
tags: [allergy, medical-record, ethan]
source: people/family/ethan.md
last_verified: 2026-03-10
---

## Known allergies
- Penicillin: rash reaction confirmed 2023 — flag to all treating doctors
- Tree nuts: mild reaction — no anaphylaxis to date
  Dr Naidoo recommends carrying EpiPen; stored in Ethan's school bag

## Dietary
- No food intolerances
- Lactose tolerant

## Review
Confirm with Dr Naidoo at next annual checkup (due ~March 2027).
```

---

### 4.11 Project

A named goal with a deadline that groups multiple related tasks.

```yaml
---
type: Project
title: Ethan's high school admission 2027
status: planning
context: [school, family]
deadline: 2026-10-01
people: [ethan, wife, mr-jacobs]
tasks_linked:
  - tasks/pending/request-school-reports.md
  - tasks/pending/book-open-days.md
  - tasks/pending/submit-application-form.md
  - tasks/pending/arrange-assessment.md
progress_pct: 15
---

## Goal
Secure admission to preferred high school for January 2027 intake.

## Key dates
- Open day: 2026-07-28
- Application deadline: 2026-10-01
- Assessment date: TBC (post application)
- Decision expected: ~2026-11-15

## Requirements
School requires:
- Last 2 years of academic reports
- Principal's reference letter
- Completed application form (available from school website)
- Application fee: R500

## Notes
First-choice school has limited intake — apply to backup by same deadline.
```

---

### 4.12 Meta

Configuration files for pipeline and LLM behaviour. Stored in `_meta/`.

#### `_meta/AGENTS.md`
```markdown
# Agent Instructions

This is a personal knowledge base processed by a local LLM (Ollama).
The following instructions apply to all automated agents reading this vault.

## Vault conventions
- All files use YAML frontmatter followed by markdown body
- Wikilinks use slug format: [[people/family/ethan]]
- Dates are ISO 8601 with timezone offset (+02:00 for SAST)
- Relative links are relative to vault root

## Priorities
When assessing task priority, apply the following order:
1. Explicit `priority` field in frontmatter
2. Context priority_weight from contexts/
3. Due date proximity (see reminder-rules.md)
4. Dependency on other people (task requires external action → escalate)

## Behaviour
- Never delete files — set status to cancelled or resolved
- Always append to message_refs, never overwrite
- Rewrite "Current understanding" and "Open questions" sections in topics on update
- If unsure of topic match, create a new topic rather than forcing a match
```

#### `_meta/reminder-rules.md`
```markdown
# Reminder Rules

Consumed by: n8n pipeline, Ollama daily digest prompt

---

## medical
Trigger: event.context includes "medical" OR task.context includes "medical"
Notify at: event.when - 3 days
Notify at: event.when - 1 day
Channel: whatsapp-self
Priority boost: task.priority -> high when due within 3 days

## school
Trigger: event.context includes "school"
Notify at: event.when - 7 days (for prep tasks)
Notify at: event.when - 1 day (reminder)
Channel: whatsapp-self

Trigger: task.context includes "school" AND task.due approaching
Notify at: task.due - 5 days
Notify at: task.due - 2 days

## finance
Trigger: task.context includes "finance" OR commitment.context includes "finance"
Notify at: due - 7 days
Notify at: due - 3 days
Priority boost: -> critical when due within 3 days

## commitment-escalation
Trigger: commitment.task_assigned == null
  AND today >= commitment.due - escalate_after_days
Action: set commitment.priority = critical
Notify: daily digest

## topic-stale
Trigger: topic.status == "active"
  AND topic.last_updated < today - 14 days
Action: flag topic for manual review
Notify: weekly digest

## family
Trigger: event.context includes "family"
Notify at: event.when - 1 day
Channel: whatsapp-self
```

#### `_meta/priority-weights.md`
```markdown
# Priority Weights

Used by Ollama to score and rank tasks in daily briefings.

## Context weights (higher = more urgent)
medical:      10
finance:      10
school:        9
family:        7
professional:  5
personal-kb:   2

## Modifier rules
+3  task has explicit `due` date within 7 days
+5  task has explicit `due` date within 2 days
+5  commitment with no task_assigned and due within 7 days
+2  task blocks another task
+1  task involves an external person (requires coordination)
-2  task.status == "blocked" (deprioritise until unblocked)

## Daily briefing output format
Surface top 5 tasks by weighted score.
Group by context.
Flag any commitments with no task_assigned.
Flag any stale topics (last_updated > 14 days).
```

---

## 5. Index File Examples

### `tasks/index.md`
```yaml
---
type: Index
title: Task Index
last_updated: 2026-06-17
---

## Pending (high priority)
- [[tasks/pending/book-ethan-flu-vaccine]] — due 2026-06-30 · medical
- [[tasks/pending/pay-school-fees-july]] — due 2026-07-01 · finance/school
- [[tasks/pending/book-venue-ethan-birthday]] — due 2026-06-20 · family

## Pending (medium priority)
- [[tasks/pending/renew-pilots-licence]] — due 2026-08-15 · flying

## In Progress
- [[tasks/in-progress/setup-n8n-whatsapp-pipeline]] — no due date · personal-kb

## Commitments without tasks
- [[commitments/school-fees-q3-2026]] — due 2026-07-01 ⚑
- [[commitments/car-service-overdue]] — overdue ⚑
```

### `topics/index.md`
```yaml
---
type: Index
title: Topic Index
last_updated: 2026-06-17
---

## Active
- [[topics/active/ethan-birthday-party-2026]] — last msg: 2026-06-17 · 3 messages · 2 open questions
- [[topics/active/car-service-overdue]] — last msg: 2026-06-12 · stale warning
- [[topics/active/school-fees-july]] — last msg: 2026-06-14 · commitment linked

## Resolved (recent)
- [[topics/resolved/zoe-dentist-booking]] — resolved 2026-06-10
```

### `channels/index.md`
```yaml
---
type: Index
title: Channel Index
last_updated: 2026-06-17
---

## WhatsApp
| Channel | Signal | Last processed | Active topics |
|---|---|---|---|
| [[channels/whatsapp/wife-direct]] | high | 2026-06-17 | 2 |
| [[channels/whatsapp/school-parents-group]] | low | 2026-06-14 | 1 |
| [[channels/whatsapp/body-corporate-group]] | low | 2026-06-10 | 0 |

## Email
| Channel | Signal | Last processed | Active topics |
|---|---|---|---|
| [[channels/email/dr-naidoo-practice]] | high | 2026-06-10 | 1 |
| [[channels/email/ethan-school-admin]] | medium | 2026-06-08 | 0 |
```

### `people/index.md`
```yaml
---
type: Index
title: People Index
last_updated: 2026-06-17
---

## Family
- [[people/family/wife]] — Partner
- [[people/family/ethan]] — Son, age 7
- [[people/family/zoe]] — Daughter, age 5

## Medical
- [[people/medical/dr-naidoo]] — Paediatrician (Ethan) · Sandton
- [[people/medical/dr-van-der-berg]] — GP (family) · Fourways

## School
- [[people/school/ms-pretorius]] — Ethan's class teacher
- [[people/school/mr-jacobs]] — School principal

## Professional
- [[people/professional/alice-du-plessis]] — Colleague
```

### `notes/index.md`
```yaml
---
type: Index
title: Notes Index
last_updated: 2026-06-17
---

## Medical
- [[notes/ethan-allergies]] — Ethan · penicillin, tree nuts · verified 2026-03-10
- [[notes/medical-aid-details]] — Discovery Health membership numbers

## Home & Family
- [[notes/recommended-electrician]] — rec from wife · no task assigned
- [[notes/school-fee-schedule-2026]] — fee amounts and payment reference

## Finance
- [[notes/insurance-renewals]] — home, car, life — renewal dates
```

---

## 6. Pipeline Design

### 6.1 Per-message flow (n8n)

```
[Trigger: message received]
        │
        ▼
[1. Write raw message to messages/{channel-slug}/{timestamp}.md]
        │
        ▼
[2. Ollama: classify message]
   → noise?        → update frontmatter noise:true, STOP
   → actionable?   → continue
        │
        ▼
[3. Ollama: topic match]
   → load topics/index.md + active topic summaries
   → match confidence > 0.75?  → link to existing topic
   → no match                  → create new topic file
        │
        ▼
[4. Ollama: extract entities]
   → tasks?        → create tasks/pending/{slug}.md
   → events?       → create events/{year}/{month}/{slug}.md
   → commitments?  → create commitments/{slug}.md
   → new people?   → create people/{context}/{slug}.md stub
        │
        ▼
[5. Ollama: update topic]
   → rewrite "Current understanding"
   → update "Open questions"
   → append to message_refs
   → update last_updated
        │
        ▼
[6. Update channel: last_processed timestamp]
        │
        ▼
[7. Update tasks/index.md and topics/index.md]
```

### 6.2 Weekly digest flow

```
[Trigger: scheduled, Sunday evening]
        │
        ▼
[1. Load tasks/index.md — pending + in-progress]
[2. Load commitments/index.md — unresolved]
[3. Load topics/index.md — active, check for stale]
[4. Apply priority-weights.md scoring]
        │
        ▼
[5. Ollama: generate weekly briefing]
   → Top tasks by priority
   → Upcoming events (next 14 days)
   → Commitments without tasks
   → Stale topics needing review
        │
        ▼
[6. Deliver via preferred channel (WhatsApp self-message / Obsidian note)]
```

---

## 7. Ollama Prompts

### 7.1 Message classification prompt

Use this as the **system prompt** for the initial classification step. Pass the raw message text as the user message.

```
You are a personal knowledge base assistant processing incoming messages for a busy professional.
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
```

---

### 7.2 Topic matching prompt

Use this after classification confirms the message is signal. Pass the message intent + existing active topic summaries.

```
You are a knowledge base assistant. Your job is to decide whether an incoming message
belongs to an existing open topic, or whether it represents a new topic.

You will be given:
1. The extracted intent of the new message
2. A list of currently active topics with their titles and "Current understanding" summaries

Respond ONLY with a JSON object:

{
  "match": true/false,
  "topic_slug": "slug of matched topic, or null if no match",
  "confidence": 0.0–1.0,
  "match_reason": "brief explanation of why this matches or why no match was found",
  "new_topic_title": "if match is false, suggest a concise title for the new topic, else null"
}

Rules:
- Only match if the new message is clearly about the same subject as an existing topic
- A confidence below 0.75 should result in match: false
- Prefer creating a new topic over forcing a weak match
- A topic about "birthday party venue" and a message about "birthday cake" are the same topic
- A topic about "school fees" and a message about "school sports day" are different topics

New message intent: {{intent}}

Active topics:
{{topics_list}}
```

---

### 7.3 Topic update prompt

Use this to rewrite the topic document body after a new message is linked. Pass the full current topic body and the new message's raw text + extracted intent.

```
You are updating a personal knowledge base topic document.
You will be given the current topic document body and a new message that has been linked to it.

Your job is to rewrite the document body to reflect the new information.
Keep the document concise. Preserve the "## Resolved" section — only add to it, never remove.

Rules:
- Rewrite "## Current understanding" to incorporate the new information naturally
- Update "## Open questions" — remove any question that the new message answers, add new ones it raises
- Add newly resolved items to "## Resolved"
- Write in plain, factual prose — no bullet points in "Current understanding" unless listing 3+ distinct items
- Do not reference the message itself ("a message was received saying...") — just update the facts

Respond ONLY with the updated markdown body (no frontmatter, no explanation).

Current topic body:
{{current_body}}

New message raw text:
{{raw_message}}

Extracted intent:
{{intent}}
```

---

### 7.4 Task creation prompt

Use this when classification confirms a task is implied. Pass the message intent and context.

```
You are creating a task entry for a personal knowledge base.
Generate the YAML frontmatter and a brief body for a new task file.

Rules:
- title: short, actionable, starts with a verb (Book, Call, Pay, Send, Review, etc.)
- status: always "pending" for new tasks
- priority: infer from context and urgency — default to "medium" if unsure
- due: include only if a specific date or timeframe was mentioned; use ISO 8601 date; null if none
- context: array — choose from [family, medical, school, finance, professional, personal-kb]
- people: array of person slugs relevant to the task (use [] if none)
- Body: 1–3 sentences of relevant detail. Include any specifics from the message (phone numbers, amounts, reference numbers) if present.

Respond ONLY with a complete markdown file (frontmatter + body). No explanation.

Message intent: {{intent}}
Raw message: {{raw_message}}
Context(s): {{contexts}}
Urgency: {{urgency}}
People mentioned: {{people}}
Source message file: {{message_filepath}}
```

---

### 7.5 Person stub creation prompt

Use this when a new person is mentioned who doesn't exist in the KB.

```
You are creating a person stub entry for a personal knowledge base.
A new person has been mentioned in a message. Create a minimal person file
based on the available information.

Rules:
- title: full name if known, role if name not known (e.g. "Ethan's Class Teacher")
- role: their relationship to the KB owner or their professional role
- context: infer from the message context
- All unknown fields should be null or omitted
- Body: 1 sentence describing who this person is and how they relate to the KB owner.
  End with: "⚠ Stub — details to be completed."

Respond ONLY with a complete markdown file (frontmatter + body). No explanation.

Person mentioned: {{person_name_or_role}}
Message context: {{message_context}}
Mentioned in: {{message_filepath}}
```

---

### 7.6 Daily briefing prompt

Use this for a scheduled morning digest. Pass current task list, upcoming events, and unresolved commitments.

```
You are generating a daily briefing for a personal knowledge base.
Be concise. The owner is a busy professional — surface only what matters today and this week.

Structure your response as plain text (not markdown headers) in this order:
1. Today's priorities — top 3 tasks, one line each
2. Upcoming in 7 days — events and task deadlines
3. Needs attention — commitments without tasks, stale topics
4. Optional: one low-priority item worth doing if time allows

Keep the whole briefing under 200 words.
Use plain language — this may be delivered as a WhatsApp message.

Current date: {{date}}

Pending tasks (sorted by priority score):
{{tasks_list}}

Upcoming events (next 7 days):
{{events_list}}

Unresolved commitments:
{{commitments_list}}

Stale topics (no activity in 14+ days):
{{stale_topics_list}}
```

---

## 8. Naming Conventions

| Concept | Format | Example |
|---|---|---|
| Person | `{firstname}-{lastname}.md` | `dr-naidoo.md` |
| Task | `{verb}-{brief-slug}.md` | `book-ethan-flu-vaccine.md` |
| Event | `{slug}-{YYYY-MM-DD}.md` | `ethan-sports-day-2026-06-20.md` |
| Channel | `{who-or-group-slug}.md` | `wife-direct.md`, `school-parents-group.md` |
| Message | `{YYYY-MM-DDTHH-MM-SS}.md` | `2026-06-15T14-17-45.md` |
| Topic | `{descriptive-slug}.md` | `ethan-birthday-party-2026.md` |
| Location | `{place-slug}.md` | `dr-naidoo-rooms.md` |

**Slug rules:**
- Lowercase only
- Hyphens, no underscores or spaces
- No special characters
- Dates in ISO format with hyphens
- Times use hyphens not colons (filesystem safe)

---

## 9. Open Questions & Future Work

- **Embedding-based topic matching:** Replace LLM topic matching with a vector similarity step using a local embedding model (e.g. nomic-embed-text via Ollama) for better performance at scale.
- **Relationship graph:** A `relationships/` directory mapping explicit person-to-person relationships (e.g. `ethan-dr-naidoo: patient-doctor`) to support relational queries.
- **Voice note ingestion:** Whisper (local) → transcript → same pipeline as WhatsApp text.
- **Calendar sync:** Events in `events/` bidirectionally synced with a local CalDAV server.
- **Archive policy:** Messages older than 90 days move to `messages/archive/{year}/`. Topics resolved more than 6 months ago move to `topics/archive/`.
- **Conflict detection:** If two tasks with the same due date are both `priority: critical`, surface a conflict in the daily briefing.

