# Comment style

Grounded in Code Complete (ch. 32), Clean Code (ch. 4), and the Microsoft
F# style guide. Every comment in this repo should survive the grading below.

## Taxonomy (McConnell)

A comment is one of: **repeat** (restates the code), **explanation**
(explains what confusing code does), **marker** (TODO/debt), **summary**
(condenses a block), **intent** (why, at problem level). Only *intent*,
*summary*, and *marker* comments earn their keep. A *repeat* comment is
deleted on sight; an *explanation* comment is a prompt to clarify the code
instead — comment only if the confusion is external (protocol quirk, MySQL
behavior), which makes it an intent comment.

## Grades

**KEEP**
- Why, not what: intent, invariants, constraints the code cannot express.
- External facts: MySQL protocol/semantics quirks, spec links, oracle-verified
  behaviors ("MySQL returns NULL here, not 0").
- Consequence warnings ("reordering these writes desyncs sequence ids").
- `ponytail:` debt markers (project convention: named ceiling + upgrade path).
- `///` XML docs on public API — one line preferred (F# style guide).

**DELETE**
- Repeats the code in English.
- Session narration / meta-commentary: references to reviews, findings,
  agents, tasks, prior versions, or the act of writing the code ("moved from
  X", "per the finding", "this now handles...", "note that we...").
- Plan/milestone references: "M9", "M10-3", roadmap phases, design-doc
  section numbers. Code and tests describe behavior, not project history —
  a test named "M10 streaming pipeline" says nothing; "LIMIT stops the scan
  once enough rows survive" does. This applies to comments, test names, and
  testList labels alike.
- Reviewer-directed justification ("this is correct because...") — if the
  claim matters, it belongs in a test.
- Journal/history comments — git owns history.
- Placeholder scaffolding comments left from stubs.

**REWRITE**
- A KEEP-worthy fact wrapped in narration: strip to the fact, present tense,
  no first person.

## Style

Present tense, declarative, no "we"/"our". No apologies, no hedging, no
"note that". A comment that needs three sentences is usually one fact plus
two sentences of fluff.

## Markdown documents

No emojis. Status markers are words ("Status: done", "Status: open") or
task-list checkboxes (`[x]`/`[ ]`), never ✅/☐/🎉. The same DELETE rules
apply to prose: no session narration, no milestone-name-as-explanation.
