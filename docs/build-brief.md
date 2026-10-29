# Workflow Workbench scope

A new, independent implementation of general configurable-approval concepts. Do not copy internal engine, UI, prompts, configurations or data. Fictional equipment-request example only.

## User story
A coordinator visually configures a workflow. Publishing creates an immutable definition version. A requester submits equipment intake; Operations and Finance review in parallel; Release waits for both. A reviewer can return the request to intake with a reason. The history preserves the prior review cycle. A new definition changes future requests while existing requests stay pinned to their original definition.

## Included
- Clickable graph and a step inspector: names, one assigned role, prerequisites, an optional required confirmation question; add/remove steps; schema/graph validation before publish.
- One start and one terminal node, acyclic connected definitions, up to 10 steps. Fan-out and AND-join behavior.
- Published immutable versions and explicitly browser-local unpublished drafts.
- SQLite-backed instances, workflow state, revision checks and append-only action history in a single transaction.
- Fixed fictional demo identities selected through a local session. The runtime takes identity/roles from a signed cookie, never the command body. This is not production identity management.
- Bounded comments/inputs, confirmed questions, send-back behavior, stale command rejection and safe repeated command handling.
- Original source, tests, screenshots, walkthrough, hosted CI and personal GitHub publication; portfolio/profile entry after validation.

## Excluded
Drag-and-drop canvas coordinates, arbitrary script evaluation, a general rule language, production SSO, email delivery, external side effects/compensation, automatic migration of active instances and a durable background worker. The original internal system has a wider event lifecycle; this demonstration does not claim to reproduce it.

## Data and transactions
PublishedVersions stores the full validated JSON definition; Instances stores a pinned version, revision, current cycle and step-state JSON; History stores actor/action/step/comment, command identity and payload hash. Each transition checks identity, prerequisites, expected revision and repeated-command intent while holding a SQLite write transaction. State and history commit together.

## Design
A clean workshop interface: ink #203544, blue #28517D, pale blue #E8F0F7, warm paper #FCFCFA, ochre #8A5800 for blocked states, green #2B6550 for complete. System Segoe UI/Bahnschrift typography. A graph is the main object; an adjacent inspector makes selected nodes editable. Separate Designer and Run views, with a visible version on both. State uses text and icons, not color alone. Real graph nodes, forms and audit events rather than a fake animated flowchart.

## Checks
Definition bounds/references/cycles; graph reachability; role enforcement; required answers; fan-out/join; return resetting other branches; version pinning; rejected writes leaving both state and history untouched; repeated/conflicting commands; concurrent stale actions; reopen/restart persistence; API and full browser walkthrough.
