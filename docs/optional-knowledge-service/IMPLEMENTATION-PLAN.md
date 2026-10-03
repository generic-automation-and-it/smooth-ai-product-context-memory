# Optional Mimi Knowledge Service implementation plan

> Execution status (October 3, 2026): Bob subsequently authorized implementation, GitHub feature-branch publication, bounded real provider calls, and installation/enablement on his existing Unraid stack. Earlier planning-only statements below describe the original document's status. See [EXECUTION.md](EXECUTION.md) for current progress and verified evidence.


Draft delivery plan, October 3, 2026. Implements the [PRD](PRD.md) using the proposed [technical design](TECHNICAL-DESIGN.md). This document plans the work; no implementation is authorized by its presence alone. Bob intends to request implementation next.

## Branch and repository approach

At implementation start, inspect the existing checkouts and fetch the current upstream main. Create an isolated feature checkout from that verified revision, preserving Bob's existing contribution branch and inspection worktrees. Proposed branch name: `codex/optional-knowledge-service`. The name is a recommendation until the implementation request confirms or supersedes it.

All feature code and committed planning documents belong on that branch. Deliver small reviewable commits per milestone; do not implement on main or release the service by default. A working feature branch is not a deployment request.

Preserve the friend's GitHub upstream and existing contribution remote. Bob's standing preference is Gitea for project source control: inspect for a matching Gitea repository before any eventual push, preserve history, and do not assume an unrelated deploy key works. If Bob explicitly directs this contribution to GitHub, follow that direction. No new repository, push, or PR is required during this planning task.

Carry this pack into the feature checkout under a stable documentation path. Align it with upstream BRD/HLD conventions when useful, without falsely marking it accepted upstream. Add links from the relevant agent context files. Do not include private vault content, runtime databases, provider secrets, or generated corpus material in Git.

## Delivery strategy

Keep the service disabled by default through every milestone. Start with a working vertical read path, then a preview-only capture path, then durable writes. Add adaptive escalation and parallel workers only after the simpler path has a measured baseline. One service and a few internal components are sufficient; do not begin with multiple independently deployed agents.

| Milestone | Deliverable | Depends on | Primary requirements |
|---|---|---|---|
| 0 | Verified baseline, contracts, and fixtures | None | KS-01 through KS-15 |
| 1 | Optional service skeleton and integration skill | 0 | KS-01, KS-02, KS-03, KS-14, KS-15 |
| 2 | Complete bounded retrieval path | 1 | KS-04 through KS-07, KS-13 |
| 3 | Capture extraction and change preview | 2 | KS-08 through KS-11 |
| 4 | Durable capture and safe commits | 3 | KS-02, KS-09 through KS-12 |
| 5 | Adaptive routing and bounded parallel work | 2 and 4 baseline evidence | KS-07, KS-13, KS-15 |
| 6 | Deployment, caller integration, and release validation | 4 and 5 | All |

## Milestone 0 Establish the baseline

Read the current repository rules, recheck the source against the inspected `bbc8553` baseline, and record material drift. Verify direct-mode build/tests and known failures before attributing changes to the feature. The earlier Windows audit found environment-dependent harness failures; preserve that distinction.

Freeze versioned request/response contracts for retrieval, capture, receipts, and clarification. Decide the additive evidence metadata shape, source attachment operation, and lifecycle mapping before code writes new records. Current `proposed`/`approved` status must not be stretched into an invented shipped-state guarantee.

Build a synthetic evaluation corpus with expected source identities, exceptions, and authoritative versus speculative statements. Include: simple lookup; paraphrased request; cross-topic question; customer exception; absent answer; conflicting evidence; approved but unshipped decision; equivalent claim; same subject in another group; late correction; repeated handoff; and conversation text containing embedded instructions.

**Exit evidence:** a recorded direct-workflow baseline, reviewed contract examples, and tests for expected lifecycle treatment. No live private corpus is required. Select an accessible standard LLM and verify Jev account access before promising a live demonstration. Without credentials, build against deterministic fakes and explicitly leave live validation outstanding.

## Milestone 1 Add the optional shell

Create the Knowledge library and Knowledge Host, configuration, health/readiness checks, provider interfaces, and an HTTP client for the core Host. Provide deterministic fake providers for tests. Add the single caller skill and thin client using the proposed high-level operations.

The application graph initially contains simple typed nodes and transitions. Add ingress secret detection and non-content diagnostics before introducing durable input storage or real provider calls. Do not route the new service through the current Understanding export command: the audit identified ignored preflight results and unconditional new-record payloads there.

**Exit evidence:** the new service can run explicitly, authenticate, and return a typed stubbed result. With the flag off and every provider credential absent, core startup, normal reads/writes, and existing clients behave as before. Verify no model traffic or extra service resource exists in disabled mode.

## Milestone 2 Deliver retrieval end to end

Implement intent resolution, seed search, bounded graph expansion, body retrieval, coverage assessment, synthesis, and citation validation. Integrate actual Jev decisions and one generative provider. Support ordinary direct lookups without mandatory model work at every node.

Use explicit temporal policy and preserve proposed/approved distinctions. Account for portable understandings when the task calls for them. Return coverage and stop reasons even when partial. Implement the shared budget ledger before retry or expansion loops, including usage reservations and timeout propagation.

**Exit evidence:** a fresh calling agent uses only the new integration skill to receive a cited brief. Tests prove no invented citation, no missing load-bearing exception in the fixture answers, no hidden timeout-as-empty result, and disclosed budget cuts. Record main-agent context and total provider use separately.

## Milestone 3 Deliver capture preview

Accept attributed handoffs, extract claims, find comparison candidates, and propose explicit create/version/link/skip/evidence operations. Use Jev for bounded relationship judgments and the LLM for extraction and nuanced interpretation. Implement the authority rules and synthetic cancellation example from the PRD.

This milestone writes no business knowledge. Group resolution must be read-only in preview; do not use the mutating resolver under a zero-mutation promise. Semantic comparison must affect the plan, rather than being printed and ignored. Include exact and paraphrased duplicate cases, and proposals that must not displace an approved current claim.

**Exit evidence:** a reviewable change set with sources, targets, reasons, and expected versions; unchanged database and blob counts; failed comparison stops affected changes; conflicts and rejected candidates remain visible.

## Milestone 4 Make capture durable

Implement the sanitized local journal, durable received acknowledgement, status polling, resumption, clarification input, and final receipts. Add the backward-compatible Host commit contract for operation idempotency and expected-version checks. Cover group creation and source attachment as well as memory and relationship updates.

The commit endpoint must return the same result after an ambiguous successful write, not merely avoid duplicate creates. A deterministic UUID does not prevent duplicate version bumps. Reconcile a direct writer's concurrent change against the original evidence before retrying.

Large handoffs may be processed as bounded explicit batches within the request budget. Report which batches committed and which remain; do not imply whole-handoff atomicity. Store durable claim evidence in the corpus before purging temporary handoff content. Provide journal retention, size limits, and pending-work backup instructions.

**Exit evidence:** kill/restart at every receipt and commit boundary, replay requests, and interrupt network responses after commit. Acknowledged work is recoverable; retries add no duplicate version or source; same-key/different-payload requests fail visibly. Snapshot/restore preserves new corpus metadata, while pending-job recovery is tested separately.

## Milestone 5 Add adaptive effort where justified

Compare the simple workflow with candidate routing improvements. Enable stronger-model interpretation for demonstrated hard cases and parallel retrieval for independent questions. Keep one merge/synthesis owner and one budget ledger. No branch receives a new budget and no worker can recursively spawn unbounded workers.

Tune Jev questions and thresholds on development fixtures, then evaluate on a held-out set. Distinguish a retrieval miss from a reasoning failure. Use observed quality and cost to keep or remove routes. A complex graph that is more expensive without better outcomes should be simplified.

**Exit evidence:** traces explain each expansion and model escalation, parallel calls remain within the shared limits, and the report compares quality, caller context, latency, and total cost with the simple workflow and direct-agent baseline. Vendor benchmark claims are not substitutes for these results.

## Milestone 6 Package and validate the optional mode

Add the service resource to the existing controller only behind the disabled-by-default flag. Package its image and persistent journal volume. Document enablement, credentials, status, safe disablement, pending-work recovery, and the difference between corpus snapshots and pending jobs. Do not launch another full controller beside a live one to test the feature; isolate the test engine or use the existing installation safely.

Finish the integration skill's automatic end-of-task handoff instructions, incremental cursor handling, and polling. Test compaction capture only in a runtime that exposes the event; document an explicit fallback otherwise. Avoid double capture when the old direct writer and the new service integration are installed together: the caller chooses one capture owner for a task.

**Exit evidence:** supported Windows and Linux/macOS paths have clear tested behavior; enabled, disabled, unavailable-provider, and restart scenarios pass. Turning the service off leaves all incorporated knowledge available through direct interfaces. A fresh agent completes a retrieval, capture, and later recall without being given Mimi's internal skills.

## Required verification matrix

| Area | Required checks |
|---|---|
| Compatibility | Service disabled; keys missing; service down; old clients; legacy records; concurrent direct edits |
| Retrieval quality | Exact and paraphrased requests; exceptions; historical/current distinction; conflicts; missing evidence; citation validity |
| Capture quality | Atomicity; semantic duplicates; source attachment; cross-group subjects; corrections; proposed versus approved intent versus shipped evidence |
| Authority | High Jev confidence cannot promote a proposal; model-generated text cannot impersonate a user decision; conflict preserves existing approved behavior |
| Durability | Crash before acknowledgement; after acknowledgement; after Host commit but before receipt; replayed handoffs; partial batches; journal full |
| Budget | Parallel reservations; retries; timeouts; unavailable usage data; oversized input; graph cycles; bounded expansion |
| Secret handling | Synthetic credential formats at ingress, retrieved content, generated output, journal, receipts, and logs; ordinary prose false positives; detector failure |
| Recovery | New metadata round-trip through export/snapshot/restore; pending-work recovery; service disable and re-enable |
| Usability | One integration skill; meaningful clarification; no database-schema knowledge required from the main agent |

Use unit tests for transitions and contracts, real-store component tests for commits and concurrency, integration tests for optional deployment, and separately recorded live-model evaluations for semantic quality. Mocked correctness and recorded prior model answers cannot close the live evaluation gate.

## Release gates and rollback

Do not enable the feature by default at release. A release candidate requires all deterministic gates above, a live-model evaluation report, documented pending-work recovery, and explicit reporting of remaining limitations. Save the baseline and held-out results so future model or prompt changes can be compared fairly.

The primary rollback is to disable the service and return to direct mode. Stop accepting new captures, drain or persist active work, and preserve pending receipts. Incorporated business knowledge remains. Additive schema changes stay compatible; do not reverse migrations or delete stored claims as an automatic rollback step.

The implementation report must identify the branch, tested commit, enabled/disabled checks, live model IDs, evaluation evidence, and any unfinished gate. Avoid claiming the system reduces overall cost unless the measurements support it.

## Mandatory collaborator amendment verification

Before the release gates above close, complete all six rows in [COLLABORATOR-REVIEW.md](COLLABORATOR-REVIEW.md). Inspect and amend current code rather than restarting milestones.

1. Add real-store attribution checks for unchanged direct metrics, service caller requests with multiple internal passes, and excluded/distinct capture-comparison events.
2. Run shared atomicity fixtures through both modes: conditional “but only” wording and equivalent wording must agree; two independently meaningful claims must still fail.
3. Read old and new evidence versions, confirm immutable old sources and preserved metadata, and replay the same operation without additional versions.
4. Verify snapshot v3/v4 and refusal cases. Restore an actual older isolated corpus, detect its new epoch, explicitly reconcile the journal, re-read/re-plan safely, then retry reconciliation and capture to prove idempotency and unchanged spending limits.
5. Prove both modes use the same redactor source and test every required boundary, including generated metadata and detector failure.
6. Record owner-decision references and fixtures separating document approval, intended changes and shipped observations. Do not close upstream open decisions by inference.

Independent review must inspect actual implementation and evidence for each row. Record pending or failed results in the KS acceptance matrix; no silent deferral to a later release.
