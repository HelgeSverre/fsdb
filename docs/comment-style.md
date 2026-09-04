# Comment style

Grounded in Code Complete (ch. 32), Clean Code (ch. 4), and the Microsoft
F# style guide. Every comment in this repo should survive the grading below.

## Taxonomy (McConnell)

Comments fall into five categories:

| Kind | Meaning | Rule |
|---|---|---|
| **Repeat** | Restates the code | Delete it. |
| **Explanation** | Explains confusing code | Clarify the code instead. |
| **Marker** | Records bounded debt | Keep it when it names the ceiling and upgrade path. |
| **Summary** | Condenses a block | Keep it when the structure cannot say the same thing. |
| **Intent** | Explains why | Keep it. |

An explanation survives only when the source of confusion is external, such
as a protocol quirk or MySQL behavior. At that point it documents intent.

## Grades

### Keep

- Why, not what: intent, invariants, constraints the code cannot express.
- External facts: MySQL protocol/semantics quirks, spec links, oracle-verified
  behaviors ("MySQL returns NULL here, not 0").
- Consequence warnings ("reordering these writes desyncs sequence ids").
- `ponytail:` debt markers (project convention: named ceiling + upgrade path).
- `///` XML docs on public API — one line preferred (F# style guide).

### Delete

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

### Rewrite

- A KEEP-worthy fact wrapped in narration: strip to the fact, present tense,
  no first person.

## Style

Use present tense and declarative language. Avoid "we", "our", apologies,
hedging, and "note that".

A comment that needs three sentences usually contains one useful fact and two
sentences of removable context.

## Markdown documents

Do not use emojis, including check-mark and box symbols, as status markers.
Use words such as "Status: done" or task-list checkboxes (`[x]` and `[ ]`).

The same DELETE rules apply to prose. Markdown documents do not need session
narration or milestone names as explanations.
