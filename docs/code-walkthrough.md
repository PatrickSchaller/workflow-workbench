# Follow a workflow through the code

## Definition to running request

1. The coordinator changes a browser-local draft. Graph links come from the `requires` arrays, rather than a separate visual model that can disagree with the engine.
2. `POST /api/versions` requires the coordinator policy. `Engine.Validate` checks bounded fields, unique keys, supported roles, valid references, cycles, one starting step and one final step. The expected current version is checked inside a SQLite write transaction before inserting a new immutable version.
3. The requester creates a run. The current published version is selected while holding the same write transaction that creates the run and its initial history. The run permanently references that version.
4. `POST /api/runs/{id}/actions` obtains the actor from the protected session cookie. The body contains a step, action, expected revision, command UUID, comment and confirmation; it cannot supply an effective role.
5. `WorkbenchStore.Apply` begins an immediate transaction and loads a consistent run. A repeated UUID must belong to the same actor and carry the same intent. New commands must match the current revision.
6. `Engine.Apply` checks the assigned role, active state, comment and confirmation. Completing a step activates waiting nodes only when every prerequisite is complete. Sending back starts a fresh step-state map and advances the review cycle.
7. The store updates state/revision and inserts history before committing. If either write fails, disposal rolls back the transaction. The history-failure test uses a real SQLite trigger to fail after the state update.

## Interesting boundaries

**Definition validation is not runtime authorization.** A well-formed graph can still name Finance as the owner of a step. The live command must be executed under the Finance demo identity, even if a different identity calls the endpoint manually.

**Published versions are not mutable drafts.** An instance's version foreign key never changes. The sample adds a Technical branch to version 2 while a version 1 request remains in progress. Updating in-flight work would require a separate migration policy, intentionally absent here.

**Rework invalidates approvals.** Sending back resets every current step. The earlier cycle's events remain visible but are not inputs into current prerequisite checks. This conservative policy is easier to explain than silently reusing previous approvals.

**Repeated command handling precedes the revision check.** A matching committed command can be safely recognized after the revision has advanced. A reused command ID with changed intent, or a different actor, is rejected. Replay returns the current run snapshot and never adds another event.

**One transaction is not distributed consistency.** State and audit writes share SQLite. External effects would need a separate design, such as an outbox and idempotent delivery or explicit compensation. This demo does not call remote services and claim they can be undone by a local rollback.

**The graph is a view of the same definition.** `graph.js` lays out dependency levels. Runtime nodes use server state; the inspector edits the actual draft. Invalid drafts remain editable, but server publication refuses them. Wide diagrams have a contained scroll region and real focusable buttons.

## Small scope choices

The app uses .NET and plain browser JavaScript to keep setup short. SQLite provides durable transactions without a database server. There is no generic repository, plug-in discovery, arbitrary expressions, drag-and-drop library or frontend build.

The identity picker is explicit simulation. ASP.NET cookie protection and role checks are real, but any local visitor can select any demo identity. Cookie keys are process-local and database data survives restarts. A shared application would need real sign-in, record visibility/ownership policy and operational key management.

The first things to revisit for a real deployment are identity/authorization, process-change governance, selective rework semantics, operational history retention, and delivery of external side effects. Adding them blindly would obscure the core behavior this repository demonstrates.
