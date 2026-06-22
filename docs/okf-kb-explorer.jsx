import { useState } from "react";

const TREE = {
  name: "personal-kb/",
  type: "root",
  children: [
    {
      name: "people/",
      type: "dir",
      note: "One file per person in your life",
      children: [
        { name: "index.md", type: "index", note: "Index of all people with relationship summary" },
        {
          name: "family/",
          type: "dir",
          children: [
            { name: "wife.md", type: "person", note: "Partner — primary contact for family comms" },
            { name: "ethan.md", type: "person", note: "Child — linked to school, medical contexts" },
            { name: "zoe.md", type: "person", note: "Child" },
          ],
        },
        {
          name: "medical/",
          type: "dir",
          children: [
            { name: "dr-naidoo.md", type: "person", note: "Paediatrician — Sandton, linked to ethan.md" },
            { name: "dr-van-der-berg.md", type: "person", note: "GP — family doctor" },
          ],
        },
        {
          name: "school/",
          type: "dir",
          children: [
            { name: "ms-pretorius.md", type: "person", note: "Ethan's class teacher" },
            { name: "mr-jacobs.md", type: "person", note: "School principal" },
          ],
        },
        {
          name: "professional/",
          type: "dir",
          children: [
            { name: "index.md", type: "index", note: "Professional contacts index" },
            { name: "alice-du-plessis.md", type: "person", note: "Colleague" },
          ],
        },
      ],
    },
    {
      name: "tasks/",
      type: "dir",
      note: "Actionable items with status & priority",
      children: [
        { name: "index.md", type: "index", note: "Master task index, sorted by priority" },
        {
          name: "pending/",
          type: "dir",
          children: [
            { name: "book-ethan-flu-vaccine.md", type: "task", note: "due: 2026-06-30 · context: medical/family · priority: high" },
            { name: "pay-school-fees-july.md", type: "task", note: "due: 2026-07-01 · context: school/finance · priority: high" },
            { name: "renew-pilots-licence.md", type: "task", note: "due: 2026-08-15 · context: flying · priority: medium" },
          ],
        },
        {
          name: "in-progress/",
          type: "dir",
          children: [
            { name: "setup-n8n-whatsapp-pipeline.md", type: "task", note: "context: personal-kb · no due date" },
          ],
        },
        {
          name: "done/",
          type: "dir",
          children: [
            {
              name: "2026-06/", type: "dir", children: [
                { name: "schedule-zoe-dentist.md", type: "task", note: "completed: 2026-06-10" },
              ]
            },
          ],
        },
      ],
    },
    {
      name: "events/",
      type: "dir",
      note: "Time-bound occurrences — appointments, school events, meetings",
      children: [
        { name: "index.md", type: "index", note: "Upcoming events index" },
        {
          name: "2026/",
          type: "dir",
          children: [
            {
              name: "06/",
              type: "dir",
              children: [
                { name: "ethan-sports-day-2026-06-20.md", type: "event", note: "when: 2026-06-20 09:00 · location: school-grounds · people: [ethan, ms-pretorius]" },
                { name: "gp-checkup-family-2026-06-25.md", type: "event", note: "when: 2026-06-25 14:00 · location: dr-van-der-berg-rooms · people: [wife, zoe]" },
              ],
            },
            {
              name: "07/",
              type: "dir",
              children: [
                { name: "ethan-birthday-party-2026-07-19.md", type: "event", note: "when: 2026-07-19 · spawned from topic: ethan-birthday-party-2026 · people: [ethan, wife]" },
                { name: "school-term-3-starts-2026-07-14.md", type: "event", note: "when: 2026-07-14 · all-day · context: school" },
              ],
            },
          ],
        },
      ],
    },
    {
      name: "channels/",
      type: "dir",
      note: "Persistent communication surfaces — one file per chat or inbox",
      children: [
        { name: "index.md", type: "index", note: "Channel index with last_processed cursors" },
        {
          name: "whatsapp/",
          type: "dir",
          children: [
            { name: "wife-direct.md", type: "channel", note: "platform: whatsapp-direct · signal_weight: high · people: [wife]" },
            { name: "school-parents-group.md", type: "channel", note: "platform: whatsapp-group · signal_weight: low · members_unknown: 34" },
            { name: "body-corporate-group.md", type: "channel", note: "platform: whatsapp-group · signal_weight: low" },
          ],
        },
        {
          name: "email/",
          type: "dir",
          children: [
            { name: "dr-naidoo-practice.md", type: "channel", note: "platform: email · signal_weight: high · people: [dr-naidoo]" },
            { name: "ethan-school-admin.md", type: "channel", note: "platform: email · signal_weight: medium" },
          ],
        },
      ],
    },
    {
      name: "messages/",
      type: "dir",
      note: "Atomic per-message ingestion records — one file per received message",
      children: [
        {
          name: "whatsapp-wife-direct/",
          type: "dir",
          note: "Mirrors channel slug — all messages from this channel",
          children: [
            { name: "2026-06-15T08-32-11.md", type: "message", note: "noise: false · topic: ethan-birthday-party-2026 · spawned_task: none" },
            { name: "2026-06-15T14-17-45.md", type: "message", note: "noise: false · topic: ethan-birthday-party-2026 · spawned_task: book-venue-ethan-birthday" },
            { name: "2026-06-17T09-05-22.md", type: "message", note: "noise: true · topic: null" },
          ],
        },
        {
          name: "whatsapp-school-parents-group/",
          type: "dir",
          children: [
            { name: "2026-06-14T07-15-03.md", type: "message", note: "noise: false · topic: ethan-sports-day-june · spawned_event: ethan-sports-day-2026-06-20" },
            { name: "2026-06-14T07-22-41.md", type: "message", note: "noise: true · topic: null" },
          ],
        },
        {
          name: "email-dr-naidoo-practice/",
          type: "dir",
          children: [
            { name: "2026-06-10T11-04-00.md", type: "message", note: "noise: false · topic: ethan-flu-vaccine · spawned_task: book-ethan-flu-vaccine" },
          ],
        },
      ],
    },
    {
      name: "topics/",
      type: "dir",
      note: "Semantic conversation units — accumulate across many messages over time",
      children: [
        { name: "index.md", type: "index", note: "Active topics — reviewed by LLM for stale/resolved status" },
        {
          name: "active/",
          type: "dir",
          children: [
            { name: "ethan-birthday-party-2026.md", type: "topic", note: "channel: wife-direct · last_updated: 2026-06-17 · open: venue not booked, catering TBD" },
            { name: "car-service-overdue.md", type: "topic", note: "channel: wife-direct · last_updated: 2026-06-12 · spawned_commitment: car-service-overdue" },
            { name: "school-fees-july.md", type: "topic", note: "channel: school-parents-group · last_updated: 2026-06-14 · spawned_task: pay-school-fees-july" },
          ],
        },
        {
          name: "resolved/",
          type: "dir",
          children: [
            { name: "zoe-dentist-booking.md", type: "topic", note: "resolved: 2026-06-10 · spawned_task: schedule-zoe-dentist (done)" },
          ],
        },
      ],
    },
    {
      name: "contexts/",
      type: "dir",
      note: "Domains for priority weighting & grouping",
      children: [
        { name: "family.md", type: "context", note: "Priority weight: high · owner: kevin" },
        { name: "medical.md", type: "context", note: "Priority weight: high · sub-context of: family" },
        { name: "school.md", type: "context", note: "Priority weight: high · sub-context of: family" },
        { name: "flying.md", type: "context", note: "Priority weight: medium · regulatory deadlines apply" },
        { name: "finance.md", type: "context", note: "Priority weight: high · deadline-sensitive" },
        { name: "personal-kb.md", type: "context", note: "Priority weight: low · background project work" },
      ],
    },
    {
      name: "commitments/",
      type: "dir",
      note: "Obligations not yet assigned a task — reviewed weekly",
      children: [
        { name: "index.md", type: "index", note: "Unresolved commitments — LLM flags approaching without a task" },
        { name: "school-fees-q3-2026.md", type: "commitment", note: "due: 2026-07-01 · no task assigned yet · context: finance/school" },
        { name: "car-service-overdue.md", type: "commitment", note: "overdue · context: family · spawned from topic: car-service-overdue" },
      ],
    },
    {
      name: "locations/",
      type: "dir",
      note: "Places referenced across people & events",
      children: [
        { name: "dr-naidoo-rooms.md", type: "location", note: "Sandton · linked to: dr-naidoo.md" },
        { name: "ethan-school.md", type: "location", note: "Linked to: ms-pretorius.md, mr-jacobs.md" },
        { name: "faor.md", type: "location", note: "OR Tambo Int. · context: flying" },
      ],
    },
    {
      name: "notes/",
      type: "dir",
      note: "Freeform catch-all — observations, recommendations, one-off facts",
      children: [
        { name: "index.md", type: "index", note: "Notes index — linked to people/events/tasks where applicable" },
        { name: "ethan-allergies.md", type: "note", note: "linked: ethan.md, dr-naidoo.md · context: medical" },
        { name: "recommended-electrician.md", type: "note", note: "rec from: wife · no task yet" },
      ],
    },
    {
      name: "projects/",
      type: "dir",
      note: "Multi-task goals spanning weeks or months",
      children: [
        { name: "ethan-school-admission-2027.md", type: "project", note: "status: planning · tasks: 4 linked · deadline: 2026-10-01" },
        { name: "personal-kb-setup.md", type: "project", note: "status: in-progress · tasks: 6 linked" },
      ],
    },
    {
      name: "_meta/",
      type: "dir",
      note: "KB configuration — consumed by n8n/Ollama pipelines",
      children: [
        { name: "AGENTS.md", type: "meta", note: "Instructions for LLM agents consuming this KB" },
        { name: "reminder-rules.md", type: "meta", note: "Recurring reminder logic — e.g. 3 days before medical events" },
        { name: "priority-weights.md", type: "meta", note: "Context priority matrix for task ranking" },
        { name: "pipeline-log.md", type: "meta", note: "Processing log — channel cursors, message counts, errors" },
      ],
    },
  ],
};

const TYPE_STYLES = {
  root:       { icon: "⬡", color: "#a78bfa" },
  dir:        { icon: "▸", color: "#7dd3fc" },
  index:      { icon: "≡", color: "#94a3b8" },
  person:     { icon: "◉", color: "#34d399" },
  task:       { icon: "◻", color: "#fbbf24" },
  event:      { icon: "◆", color: "#f472b6" },
  channel:    { icon: "⇌", color: "#38bdf8" },
  message:    { icon: "·", color: "#475569" },
  topic:      { icon: "◎", color: "#c084fc" },
  context:    { icon: "◈", color: "#67e8f9" },
  commitment: { icon: "⚑", color: "#fb923c" },
  location:   { icon: "⊙", color: "#86efac" },
  note:       { icon: "◌", color: "#e2e8f0" },
  project:    { icon: "⬡", color: "#818cf8" },
  meta:       { icon: "⚙", color: "#64748b" },
};

const LEGEND = [
  { type: "person",     label: "Person" },
  { type: "task",       label: "Task" },
  { type: "event",      label: "Event" },
  { type: "channel",    label: "Channel" },
  { type: "message",    label: "Message" },
  { type: "topic",      label: "Topic" },
  { type: "context",    label: "Context" },
  { type: "commitment", label: "Commitment" },
  { type: "location",   label: "Location" },
  { type: "note",       label: "Note" },
  { type: "project",    label: "Project" },
  { type: "meta",       label: "Meta" },
  { type: "index",      label: "Index" },
];

const SAMPLES = [
  {
    label: "people/medical/dr-naidoo.md",
    type: "person",
    color: "#34d399",
    code: `---
type: Person
title: Dr. Priya Naidoo
role: Paediatrician
context: [medical, family]
location: locations/dr-naidoo-rooms.md
people_linked: [ethan]
channel: channels/email/dr-naidoo-practice.md
phone: "+27 11 xxx xxxx"
tags: [doctor, paeds, sandton]
---

Ethan's paediatrician since 2022.
Referral required for specialists.
Accepts Discovery Health.
Rooms in Sandton — allow 30min travel from home.`,
  },
  {
    label: "tasks/pending/book-ethan-flu-vaccine.md",
    type: "task",
    color: "#fbbf24",
    code: `---
type: Task
title: Book Ethan's flu vaccine
status: pending
priority: high
due: 2026-06-30
context: [medical, family]
people: [ethan, dr-naidoo]
topic: topics/active/ethan-flu-vaccine.md
source_message: messages/email-dr-naidoo-practice/2026-06-10T11-04-00.md
project: projects/ethan-school-admission-2027.md
---

Ethan needs his annual flu vaccine before
school term 3. Dr Naidoo's rooms confirmed
availability this month.`,
  },
  {
    label: "events/2026/07/ethan-birthday-party-2026-07-19.md",
    type: "event",
    color: "#f472b6",
    code: `---
type: Event
title: Ethan's Birthday Party
when: 2026-07-19T14:00:00Z
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

Ethan's 8th birthday party.
Venue confirmed: Acrobranch Northgate.
~12 kids attending. Cake to be collected
morning of event.`,
  },
  {
    label: "channels/whatsapp/wife-direct.md",
    type: "channel",
    color: "#38bdf8",
    code: `---
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
High signal — most messages are actionable.
Process immediately on receipt.`,
  },
  {
    label: "messages/whatsapp-wife-direct/2026-06-15T14-17-45.md",
    type: "message",
    color: "#475569",
    code: `---
type: Message
channel: channels/whatsapp/wife-direct.md
timestamp: 2026-06-15T14:17:45Z
sender: wife
noise: false
topic: topics/active/ethan-birthday-party-2026.md
spawned_tasks:
  - tasks/pending/book-venue-ethan-birthday.md
processed_by: ollama/llama3.2
---

## Raw
"Can you call Acrobranch tomorrow and check
if they have availability on the 19th?"

## Extracted intent
Action required: call venue to confirm
availability for 19 July birthday party.`,
  },
  {
    label: "topics/active/ethan-birthday-party-2026.md",
    type: "topic",
    color: "#c084fc",
    code: `---
type: Topic
title: Ethan's birthday party planning
status: active
context: [family]
channel: channels/whatsapp/wife-direct.md
people: [wife, ethan]
first_seen: 2026-06-10T19:44:00Z
last_updated: 2026-06-17T09:05:22Z
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
~12 kids. Invitations sent. Cake TBD.

## Open questions
- Cake not yet ordered
- Catering/snacks decision outstanding`,
  },
  {
    label: "contexts/medical.md",
    type: "context",
    color: "#67e8f9",
    code: `---
type: Context
title: Medical
parent_context: contexts/family.md
priority_weight: high
deadline_sensitive: true
tags: [health, appointments, prescriptions]
people_linked:
  - people/medical/dr-naidoo.md
  - people/medical/dr-van-der-berg.md
channels_linked:
  - channels/email/dr-naidoo-practice.md
reminder_rule: "_meta/reminder-rules.md#medical"
---

Medical context covers all health-related
tasks, appointments, and records for the
family. Regulatory or prescription deadlines
should be escalated to priority: critical.`,
  },
  {
    label: "commitments/car-service-overdue.md",
    type: "commitment",
    color: "#fb923c",
    code: `---
type: Commitment
title: Car service overdue
status: unresolved
priority: medium
context: [family]
overdue: true
due: 2026-05-31
topic: topics/active/car-service-overdue.md
task_assigned: null
escalate_after_days: 7
---

Service interval passed end of May.
No task assigned yet — waiting on
availability to book with dealer.

Escalate to high priority if no task
assigned within 7 days.`,
  },
  {
    label: "locations/dr-naidoo-rooms.md",
    type: "location",
    color: "#86efac",
    code: `---
type: Location
title: Dr. Naidoo's Rooms
address: "Suite 4, Sandton Medi-Centre,
  Rivonia Rd, Sandton, 2196"
coordinates: [-26.1077, 28.0567]
context: [medical]
people_linked: [dr-naidoo]
parking: "Basement P2 — first 30min free"
travel_time_from_home_min: 25
tags: [medical, sandton]
---

Rooms in Sandton Medi-Centre.
Reception: +27 11 xxx xxxx.
Book at least 2 weeks in advance for
non-urgent appointments.`,
  },
  {
    label: "notes/ethan-allergies.md",
    type: "note",
    color: "#e2e8f0",
    code: `---
type: Note
title: Ethan's allergies & dietary notes
context: [medical, family]
people_linked: [ethan, dr-naidoo]
tags: [allergy, medical-record]
source: people/family/ethan.md
last_verified: 2026-03-10
---

## Known allergies
- Penicillin: rash reaction (2023)
- Tree nuts: mild — no anaphylaxis to date
  but Dr Naidoo recommends carrying EpiPen

## Dietary
- No known food intolerances
- Lactose tolerant

## Notes
Review with Dr Naidoo at next annual check.`,
  },
  {
    label: "projects/ethan-school-admission-2027.md",
    type: "project",
    color: "#818cf8",
    code: `---
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
Secure admission to preferred high school
for January 2027 intake.

## Key dates
- Open day: 2026-07-28
- Application deadline: 2026-10-01
- Assessment: TBC (post application)

## Notes
School requires last 2 years of reports
plus principal's reference.`,
  },
  {
    label: "_meta/reminder-rules.md",
    type: "meta",
    color: "#64748b",
    code: `---
type: Meta
title: Reminder rules
consumed_by: [n8n, ollama]
---

## Rules

### medical
Trigger: event.context includes "medical"
Notify: event.when - 3 days
Notify: event.when - 1 day
Channel: whatsapp-self

### commitment-escalation
Trigger: commitment.task_assigned == null
  AND today > commitment.due + escalate_after_days
Action: set commitment.priority = high
Notify: daily digest

### task-due-soon
Trigger: task.status != done
  AND task.due <= today + 2
Action: surface in daily briefing
Priority boost: +1 level

### topic-stale
Trigger: topic.status == active
  AND topic.last_updated < today - 14
Action: flag for manual review
Notify: weekly digest`,
  },
];

const SAMPLE_GROUPS = [
  { label: "People",      types: ["person"] },
  { label: "Tasks",       types: ["task"] },
  { label: "Events",      types: ["event"] },
  { label: "Channels",    types: ["channel"] },
  { label: "Messages",    types: ["message"] },
  { label: "Topics",      types: ["topic"] },
  { label: "Contexts",    types: ["context"] },
  { label: "Commitments", types: ["commitment"] },
  { label: "Locations",   types: ["location"] },
  { label: "Notes",       types: ["note"] },
  { label: "Projects",    types: ["project"] },
  { label: "Meta",        types: ["meta"] },
];

function Node({ node, depth = 0, forceOpen = false }) {
  const isDir = node.type === "dir" || node.type === "root";
  const [open, setOpen] = useState(forceOpen || depth < 1);
  const [hovered, setHovered] = useState(false);
  const style = TYPE_STYLES[node.type] || TYPE_STYLES.note;

  // sync forceOpen
  useState(() => { if (forceOpen) setOpen(true); }, [forceOpen]);

  return (
    <div style={{ marginLeft: depth === 0 ? 0 : 18 }}>
      <div
        onClick={() => isDir && setOpen(o => !o)}
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
        style={{
          display: "flex",
          alignItems: "flex-start",
          gap: 8,
          padding: "3px 6px",
          borderRadius: 5,
          cursor: isDir ? "pointer" : "default",
          background: hovered ? "rgba(255,255,255,0.04)" : "transparent",
          transition: "background 0.12s",
          userSelect: "none",
        }}
      >
        <span style={{
          color: style.color,
          fontFamily: "monospace",
          fontSize: 13,
          minWidth: 16,
          marginTop: 1,
          display: "inline-block",
        }}>
          {isDir ? (open ? "▾" : "▸") : style.icon}
        </span>
        <div style={{ flex: 1, minWidth: 0 }}>
          <span style={{
            fontFamily: "monospace",
            fontSize: 13,
            color: isDir ? "#e2e8f0" : style.color,
            fontWeight: isDir ? 600 : 400,
          }}>
            {node.name}
          </span>
          {node.note && (
            <span style={{
              fontFamily: "ui-sans-serif, system-ui, sans-serif",
              fontSize: 11,
              color: "#4b5563",
              marginLeft: 10,
            }}>
              — {node.note}
            </span>
          )}
        </div>
      </div>
      {isDir && open && node.children && (
        <div style={{
          borderLeft: "1px solid rgba(255,255,255,0.06)",
          marginLeft: 7,
        }}>
          {node.children.map((child, i) => (
            <Node key={i} node={child} depth={depth + 1} forceOpen={forceOpen} />
          ))}
        </div>
      )}
    </div>
  );
}

export default function App() {
  const [treeKey, setTreeKey] = useState(0);
  const [expanded, setExpanded] = useState(false);
  const [activeTab, setActiveTab] = useState(0);

  const handleExpand = () => { setExpanded(true); setTreeKey(k => k + 1); };
  const handleCollapse = () => { setExpanded(false); setTreeKey(k => k + 1); };

  return (
    <div style={{
      minHeight: "100vh",
      background: "#0f1117",
      color: "#e2e8f0",
      fontFamily: "ui-sans-serif, system-ui, sans-serif",
      padding: "32px 24px",
    }}>
      <div style={{ maxWidth: 860, margin: "0 auto" }}>

        {/* Header */}
        <div style={{ marginBottom: 24 }}>
          <div style={{
            fontSize: 10,
            fontFamily: "monospace",
            color: "#38bdf8",
            letterSpacing: "0.14em",
            textTransform: "uppercase",
            marginBottom: 6,
          }}>
            Open Knowledge Format · Personal KB
          </div>
          <h1 style={{ fontSize: 20, fontWeight: 700, color: "#f1f5f9", margin: "0 0 6px" }}>
            Directory Structure
          </h1>
          <p style={{ fontSize: 12, color: "#4b5563", margin: 0, lineHeight: 1.5 }}>
            Channel/message/topic model. Channels are persistent; messages are atomic ingestion records; topics accumulate semantic meaning across messages over time.
          </p>
        </div>

        {/* Legend */}
        <div style={{
          display: "flex",
          flexWrap: "wrap",
          gap: "5px 14px",
          marginBottom: 20,
          padding: "10px 14px",
          background: "rgba(255,255,255,0.025)",
          borderRadius: 8,
          border: "1px solid rgba(255,255,255,0.06)",
        }}>
          {LEGEND.map(({ type, label }) => {
            const s = TYPE_STYLES[type];
            return (
              <div key={type} style={{ display: "flex", alignItems: "center", gap: 5 }}>
                <span style={{ color: s.color, fontFamily: "monospace", fontSize: 12 }}>{s.icon}</span>
                <span style={{ fontSize: 11, color: "#6b7280" }}>{label}</span>
              </div>
            );
          })}
        </div>

        {/* Controls */}
        <div style={{ display: "flex", gap: 8, marginBottom: 14 }}>
          {[
            { label: "Expand all", action: handleExpand },
            { label: "Collapse", action: handleCollapse },
          ].map(({ label, action }) => (
            <button key={label} onClick={action} style={{
              padding: "4px 12px",
              fontSize: 11,
              fontFamily: "monospace",
              background: "rgba(255,255,255,0.05)",
              border: "1px solid rgba(255,255,255,0.09)",
              borderRadius: 5,
              color: "#6b7280",
              cursor: "pointer",
            }}>
              {label}
            </button>
          ))}
        </div>

        {/* Tree */}
        <div style={{
          background: "rgba(255,255,255,0.015)",
          border: "1px solid rgba(255,255,255,0.07)",
          borderRadius: 10,
          padding: "14px 12px",
          marginBottom: 28,
        }}>
          <Node key={treeKey} node={TREE} depth={0} forceOpen={expanded} />
        </div>

        {/* Sample frontmatter */}
        <div>
          <div style={{
            fontSize: 10,
            fontFamily: "monospace",
            color: "#38bdf8",
            letterSpacing: "0.14em",
            textTransform: "uppercase",
            marginBottom: 12,
          }}>
            Sample frontmatter
          </div>

          {/* Type pills */}
          <div style={{ display: "flex", flexWrap: "wrap", gap: 6, marginBottom: 14 }}>
            {SAMPLE_GROUPS.map((g) => {
              const sample = SAMPLES.find(s => g.types.includes(s.type));
              const isActive = sample && SAMPLES[activeTab].type === sample.type;
              const s = TYPE_STYLES[g.types[0]];
              return (
                <button
                  key={g.label}
                  onClick={() => {
                    const idx = SAMPLES.findIndex(s => g.types.includes(s.type));
                    if (idx >= 0) setActiveTab(idx);
                  }}
                  style={{
                    display: "flex",
                    alignItems: "center",
                    gap: 5,
                    padding: "4px 10px",
                    fontSize: 11,
                    fontFamily: "monospace",
                    background: isActive ? "rgba(255,255,255,0.08)" : "rgba(255,255,255,0.025)",
                    border: `1px solid ${isActive ? s.color + "55" : "rgba(255,255,255,0.07)"}`,
                    borderRadius: 20,
                    color: isActive ? s.color : "#4b5563",
                    cursor: "pointer",
                    transition: "all 0.12s",
                  }}
                >
                  <span style={{ fontSize: 11 }}>{s.icon}</span>
                  {g.label}
                </button>
              );
            })}
          </div>

          {/* Panel */}
          <div style={{
            background: "#0d1117",
            border: "1px solid rgba(255,255,255,0.08)",
            borderRadius: 8,
            overflow: "hidden",
          }}>
            <div style={{
              padding: "7px 14px",
              background: "rgba(255,255,255,0.025)",
              borderBottom: "1px solid rgba(255,255,255,0.06)",
              fontSize: 10,
              fontFamily: "monospace",
              color: SAMPLES[activeTab].color,
              display: "flex",
              alignItems: "center",
              gap: 8,
            }}>
              <span>{TYPE_STYLES[SAMPLES[activeTab].type]?.icon}</span>
              {SAMPLES[activeTab].label}
            </div>
            <pre style={{
              margin: 0,
              padding: "14px",
              fontSize: 11,
              fontFamily: "monospace",
              color: "#94a3b8",
              whiteSpace: "pre-wrap",
              lineHeight: 1.65,
              maxHeight: 380,
              overflowY: "auto",
            }}>
              {SAMPLES[activeTab].code}
            </pre>
          </div>
        </div>

      </div>
    </div>
  );
}
