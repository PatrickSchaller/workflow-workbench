# Validation record

Local checks on October 29, 2026: Windows, .NET SDK 10.0.101, Node.js 22.18.0 and Playwright 1.64.1.

- Release compilation succeeds with warnings treated as errors; publish packaging succeeds.
- **31 .NET tests pass.** Definition reference/cycle validation, role and confirmation gates, both branch-completion orders, send-back reset, version pinning, reopen persistence, repeated/conflicting commands, concurrent revision checks, foreign-database rejection, and signed-cookie/API policy enforcement are covered.
- A real SQLite trigger deliberately rejects the history insert after the state update. The test verifies that revision, step state and history all remain unchanged.
- API checks exercise real cookie sign-in, reject a forged cookie and a cookie from a separate app instance, enforce coordinator publication, reject role-body injection, and check Host/custom-header boundaries.
- **Four Chromium checks pass.** The main walkthrough edits and publishes a new branch, keeps an earlier run pinned, sends a request back, completes two versions, exports JSON and reloads persisted history. Other checks cover invalid graphs/read-only roles, 390px diagram containment, and inert rendering of user-supplied HTML-looking titles.
- Documentation screenshots and walkthrough JSON come from API-created synthetic state in a separate scratch database. The browser test database and generated runtime databases are excluded from source control.

Local restore used the existing certificate-validated feed of public NuGet packages because this machine's sandbox .NET HTTPS environment cannot restore directly. Hosted CI uses ordinary NuGet with pinned lock files.

The .NET 10 data-protection startup service also warms a key ring even when an ephemeral provider is selected. The demo therefore uses the public XML-repository interface with a process-local memory repository, keeping both cookie protection and startup away from a shared user key directory. A new app instance cannot read a previous instance's cookie.

No employer systems, private engine files, customer requests, production sign-in or notification delivery were used. Chromium was tested; other browser engines and a full assistive-technology audit were not. This is not a load test or an evaluation of the wider internal system's event/compensation lifecycle.

Hosted Windows/Linux build, test and publish checks, plus Linux Chromium results, are available in the repository's **Build and verify** Actions workflow.
