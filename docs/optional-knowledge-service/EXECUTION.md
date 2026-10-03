# Execution and acceptance

Updated October 3, 2026. Status: implementation underway; no deployment or acceptance claim yet.

Bob explicitly authorized implementation, feature-branch commits/pushes to GitHub, bounded real provider calls, and enablement on his existing Unraid installation. This supersedes the planning-only language in the original pack. Main owns integration and live deployment. Sol builders own the core boundary and optional service; Astra provides independent review.

## Baseline and plan

- Planning baseline: `bbc85530a1ffb0f3d3ec4d1799dba6c2bbcfd9dc`.
- Refreshed upstream: `dd97d85d40cc219873668843141a9c7e746af36c`; intervening change removes Heimdallr autofill from Kvasir import (#172).
- Isolated branch: `codex/optional-knowledge-service`. Existing inspection checkouts preserved. Windows checkout uses `core.symlinks=false` due to unavailable symlink privilege; link files remain unchanged in Git.
- Existing deployment inspected read-only: one controller with Host, PostgreSQL/AGE, MinIO, Seq. No second controller will run on that Docker engine.
- Git Credential Manager provides authenticated upstream push permission; GitHub CLI has no standalone login. Use existing credential in-process without printing it.
- Provider source file located through Windows redirected Desktop API; values never enter repository or model-visible output.

1. Freeze core/service contracts; record direct baseline and synthetic evaluation expectations before tuning.
2. Implement transactional core replay/CAS/restore protection and optional service with durable budgets.
3. Implement caller skill, packaging and isolated test harness; validate real provider integrations.
4. Run independent implementation review and deterministic/live evaluation; resolve findings.
5. Back up and verify existing installation; deploy artifacts tied to tested commit, install caller integration, demonstrate acceptance.
6. Publish/verify branch commit, record operating and rollback instructions and actual measurements.

## Acceptance matrix

Implementation gates use independently inspected tests and stored records. Deployment and fresh-caller gates remain explicitly open until verified.

| Requirement | Implementation and evidence | Status |
|---|---|---|
| KS-01 | Disabled host needs no keys; real service outage preserves direct read/write/body access | Implementation verified; enabled deployment pending |
| KS-02 | Same core records and APIs; actual incorporated evidence survives optional-service outage | Verified in isolated real stores; deployment pending |
| KS-03 | One caller skill, stable handoff/cursor and receipt/reconcile commands; 12 caller tests | Fresh installed caller pending |
| KS-04 | Scoped retrieval and graph traversal; 12 real-store attribution tests, separate purposes and caller/pass counts | Verified |
| KS-05 | Six held-out live cases preserve 10/10 required details and valid citations; independent body review | Verified |
| KS-06 | Three epistemic regression cases, limits and partial/absence distinctions; original failed case retained | Verified |
| KS-07 | Bounded conditional workflow, concrete repair reasons, merged evidence, persisted limits | Independently verified |
| KS-08 | Namespaced full-message evidence identities; OWNER-DECISIONS trace and immutable attachments | Independently verified; deployment pending |
| KS-09 | Canonical semantic atomicity, 16 shared fixtures; live conditional claim and four capture regressions | Verified |
| KS-10 | Hostile authority tests; actual proposal, approved-unshipped and correction records | Independently verified; deployment pending |
| KS-11 | Immutable historical evidence, CAS, proposal/conflict protection and same-target coalescing | Independently verified |
| KS-12 | 13 real-store boundary tests; v4 snapshots/v3 compatibility, refusal tests; seven extended real recovery checks | Verified; deployment pending |
| KS-13 | Durable reservations, absolute deadline across restart, explicit audited continuation; actual SIGKILL | Verified |
| KS-14 | One canonical Python redactor packaged in service; ingress/retrieval/provider/journal/write/receipt tests | Independently verified |
| KS-15 | Readiness, classified failures, model IDs, per-pass legacy metrics and separate caller counts | Verified; deployed readiness pending |

Additional contract gates: BRD-004/HLD-008 document additive Host behavior. Mixed existing/new batches and 25 candidates across interruptions are independently tested; omitted tails are explicit, and more than 100 extracted claims are refused. Understanding export is excluded from this feature path. Upstream main checked at1fbe8d7 on October3: no new fix for the separate preflight/unconditional-create defect was found.

## Evaluation contract

Measure quality, caller instructions/context/tool turns, total provider tokens and estimated price, and wall latency separately. Use synthetic corpus and distinct held-out tasks; freeze expected facts, exception coverage, unsupported-claim and incorrect-merge criteria before tuning. Do not claim cost savings without measured evidence. Live providers must be evaluated on the deployed system in addition to deterministic tests.

## Recovery and publication

Verified production snapshot and protected configuration backup exist; concrete image rollback scripts are prepared. Final tested/published commit, deployed image identity and fresh-caller demonstration remain pending. Service disablement must preserve journal and incorporated knowledge. Do not reverse additive database migrations as routine rollback.

## Progress checkpoint

- Baseline solution build passed (two pre-existing Aspire CLI warnings). Domain 32, Application 209, Host 41 and AppHost 39 unit tests passed; Host has one existing skip. Full baseline Infrastructure component suite passed: 185 tests and eight opt-in skips.
- Isolated real PostgreSQL/AGE and MinIO stores run on Unraid, with no production data mounts and SSH-forwarded loopback test ports. No second controller was started.
- Original production snapshot passed offline verification, SHA256 `7ecf77013033e34ef14d65caf6d1c43af900dad05f40d9db532f95800c70c9cd`. Protected runtime/configuration backup was saved before any production replacement.
- Core builder reports existing15 write tests, new8 boundary tests and archive checks passing; main independent review ongoing. Expanded graph test caught AGE mutation bypassing SQL statement triggers; explicit revision increments were added, awaiting final evidence.
- Main independently ran caller7 and evaluation7 tests successfully. Evaluation fixes include current capture observation time, corpus version/source comparison after replay, required synthetic ticket URLs, modern completion-token parameter, refusal of incomplete baseline responses, and `store:false`.
- Synthetic fixtures frozen at SHA256 `74ffd537a0e72327be3ed77c256ae70e212bf8b9edb6d87087d2117b6bd52a28`. First development live run: direct retrieved all 3 expected details and cited both required sources; service found no evidence. This is a failed quality gate; query planning is being corrected. Held-out tests have not been run or tuned against.
- Astra review found purge/clarification lost-update race, negated authority phrase handling, resolved-group selector mismatch, customer scope leakage through neighbors, missing-body legacy behavior, evidence retention on source attachment, clarification selectors, and historical export evidence. Owners are resolving these; none is considered closed solely on an implementation claim.
- Bob offered his private work-domain wiki for a separate realism check. Main inspected its operating guide/index and selected two source notes. Private content and evaluation outputs stay outside the feature repository and GitHub.
- Local isolated HTTP integration runs from published copies outside Git; final deployment will be rebuilt from the final tested commit.

### Integrated live checkpoint

- Main independently passed all 13 new real PostgreSQL/AGE corpus-boundary tests after a successful integrated build. Astra's 16 hostile service component tests also pass; these are not substitutes for process-kill evidence.
- Corrected development retrieval preserves all three expected details and both sources. Six frozen held-out live cases were independently reviewed: both direct and service answers preserve all ten substantive requirements, with no unsupported claims or invalid citations. Literal automated scoring understates the direct baseline.
- Held-out caller-context estimate fell from 10,425 to 4,678 tokens (55%); caller tool turns fell from 24 to 6 (75%). Service provider usage increased (30,005 input / 4,087 output versus 5,117 / 1,639) and aggregate latency rose from 18.96 to 48.08 seconds. These measurements do not support a cost-savings claim.
- Isolated Linux core and service now run on Unraid with synthetic-only stores. Initial live capture exposed strict SourceInput serialization rejection and abbreviated model authority quotations. All four capture cases remained partial; capture acceptance remains open while fixes and independent adversarial review proceed.
- Real process-kill, budget recovery and snapshot restore verification is being prepared against the isolated stores. Production remains unchanged after its verified backup.
### Recovery and review checkpoint

- Actual isolated Linux process-kill acceptance passed six checks with independent Astra methodology review: durable acknowledgement, direct outage read/write/body compatibility, persistent uncertain-call reservations, kill between core receipt and journal save, exact replay, and real older-snapshot restore with epoch reconciliation pause. Successful run used three jobs; preparation used two more. Harness and public method summary are under `scripts/knowledge-evaluation/`.
- Disabled standalone host started without any provider/core credentials and returned 503 for readiness and context; ordinary core process is independent. No production service has been started.
- Caller namespace update independently passes10 tests; evaluation+crash guards pass12. Duplicate automated scoring now requires actual processed incorporation and evidence change, preventing a failed no-op from appearing successful.
- Capture regression v2 processed suggestion and approved-unshipped correctly. Review found same-target evidence version inflation, confidence loss and overbroad approval meaning; repairs and extra hostile tests are in progress. Novel epistemic first run2/3, with remaining failure classified OpenAI incomplete rather than an unsupported answer. Final capture acceptance stays open.
- Prepared protected production service/caller settings and concrete local startup/rollback scripts. Scripts pass Bash syntax checks. Production core, source corpus and controller have not been replaced.


### User collaborator amendment

All six new requirements are mandatory and tracked in COLLABORATOR-REVIEW.md. Core owner handles recall attribution and shared atomicity; service owner handles explicit recovery reconciliation, evidence identity/approval semantics and shared redactor boundaries. Astra independently verifies all six. Existing fixes remain in place; deployment completion is blocked until these amendments pass. PRD, technical design and implementation plan updated in both planning and feature packs.

Upstream refreshed and feature checkout fast-forwarded to 1fbe8d7 while preserving all work. No independent Understanding export fix found in fetched main. BRD-004 and HLD-008 now document explicit additive Host contracts. Mixed existing/new and >20 accounted candidate acceptance added; the publication/deployment authorization remains unchanged.


Integration tightened the original proposed per-attempt capture timeout into a durable first-processing deadline, matching the execution objective that retries/restart share enforced budgets. An explicit bounded continuation may extend it; restart alone may not. Independent regression added before final acceptance.


## Current integration checkpoint

The integrated solution builds with zero errors and two existing Aspire warnings. Independent service coverage is40 tests; builder service coverage 20. Extended live recovery passed all 7 checks across3 retained captures (8 acknowledged recovery jobs overall), including explicit epoch-pinned reconciliation, preserved prior spend, a separately granted60-second deadline extension, new operation generations and unchanged replay. Reports retain earlier failed attempts and the corrected duplicate oracle; this is resumed acceptance, not a claim of a single clean run.

The document-approval follow-up now extracts the correct distinctions but core commit rejects the new category with400. No records were written. The missing core category contract entry is being corrected and will be independently retested before deployment.

Document-approval continuation now processed the original handoff in one atomic commit. Stored records distinguish document approval, draft/unapproved child intent, and absence of implementation/release acceptance. A stale saved plan was refused after an intervening write; explicit time-only continuation re-extracted/recompared within the original12-call allowance (10 calls total). Replay leaves every record/version unchanged. Independent final semantic review is recorded in VALIDATION.md.

Final revision-continuation regression passes: a same-epoch direct edit invalidates an uncommitted comparison plan, freshly compares retained validated candidates, preserves named omitted tails and the existing ledger/deadline, and leaves ambiguous pending commits to idempotent receipt replay first. Main independently reran all40 service component tests.
