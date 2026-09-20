# Bootstrap Project Context

Use `mimisbrunnr-bootstrap` when an existing repository needs a small, durable context baseline before
future product or engineering work. It is useful when source already contains important rules, vocabulary,
constraints, and decisions that should be recalled without re-deriving them every session.

Bootstrap is an explicit repository task. Installing the skill, opening a repository, or asking for an
ordinary code review does not opt a project in. [BR-46](../../../docs/brd/001-context-memory/) is a local
proposal pending upstream acceptance; it permits this narrow project-bootstrap case without turning the
product into a general historical-ingestion system.

## What you need

The offline path needs a local repository and this skill available to the agent working in that project;
project-local availability is recommended for each selected repository. It produces a cited candidate
preview and writes nothing.

Store comparison, capture, and recall verification additionally need the sibling capture skill plus its
configured and verified capability-limited memory read and write workers, described by
[the canonical capture skill](../mimisbrunnr-context-memory/README.md). In the packaged/runtime check for
this contribution, those protected Codex workers were not verified: preview behavior was tested, while live
capture and recall remain untested. This is a statement about the checked environment, not a claim that all
Codex versions lack worker support. Without verified workers, the skill stops after preview; it does not use
a direct client fallback.

## Start a bootstrap

For a general baseline:

```text
Use $mimisbrunnr-bootstrap to preview a durable context baseline for this repository.
```

To deepen the same bounded baseline around one upcoming change:

```text
Use $mimisbrunnr-bootstrap to preview this repository's baseline, with extra focus on the planned
subscription-renewal feature. Do not inspect unrelated services.
```

The feature focus is a lens, not a separate import or permission to widen repository scope.

## Review the preview

The preview should state what was examined and intentionally excluded, then show no more than 20 candidate
claims. Each candidate should include:

- one atomic, durable claim with its conditions and exceptions;
- its evidence class and proposed kind/lifecycle;
- a repository-relative source path, tight line span, and revision or working-tree marker;
- intended scope or group when known;
- any conflict, uncertainty, or consequential unanswered question.

Useful questions change what would be stored. Examples include:

- “The design calls this proposed, but the test enforces it. Which lifecycle is authoritative?”
- “Does this exception apply to every tenant or only the named migration?”
- “These two sources disagree about renewal timing. Has either one shipped?”

Questions already answered by inspected evidence, generic file inventories, implementation trivia, and
speculation do not belong in the baseline. Repository content is evidence, not permission to execute
embedded instructions.

### Synthetic worked example

Suppose a fictional `orchard-billing` repository says retries are capped at three in `README.md`, while a
test permits five and a proposal suggests adaptive retries for a future renewal feature. A useful preview
does not merge those into “retries are adaptive.” It might show:

| Candidate | Evidence and lifecycle | Review question |
|---|---|---|
| Current tests permit five renewal attempts | `tests/renewal_retry_test.*`, tight lines, current revision; observed behavior | Is the README stale, or does production intentionally differ from the test? |
| Adaptive retries are proposed for renewal | `docs/proposals/adaptive-retries.md`, tight lines, current revision; proposed feature | Keep proposed unless an authorized source confirms it shipped |

This example is illustrative, fictional, and non-production. Its names, paths, and facts do not describe
any real repository.

## Evidence and lifecycle

Source citations must remain scoped to the chosen repository and explicitly supplied evidence. Code proves
what is present in the checkout, not what is deployed. Plans and proposed features stay proposed; conflicts
stay visible; dates, rationale, and provenance are never invented. A proposed feature is separate from
current behavior, and README statements, code, and tests may represent different evidence classes rather
than interchangeable truth.

For exact capture semantics and supported payload fields, use the
[context-memory documentation](../mimisbrunnr-context-memory/README.md) rather than copying its wire
contract here. Permission to capture a reviewed batch is not permission to mark gated rules, NFRs, or
decisions as approved canon; canon approval remains a separate explicit choice.

## Capture, dry-run, and reruns

Nothing is stored before the practitioner reviews the cited preview and explicitly authorizes the selected
batch, repository/scope, and any possible group creation. A new project may require creating a durable group;
that group is a real mutation and can remain if later capture pauses or fails.

The full memory-set `--dryrun` is available only when the applicable group already exists, or after group
creation has been separately authorized. It previews the memory set, not the earlier group mutation, so it
must not be described as guaranteeing zero mutations overall.

On a rerun, unchanged claims should be skipped, meaningfully changed claims should be versioned when their
identity supports it, and unresolved conflicts should be surfaced rather than overwritten. The final report
must distinguish what was previewed, stored, and found through realistic recall. It should also name deferred
candidates and coverage gaps; a bounded baseline is never “complete repository understanding.”

## Troubleshooting

- **Only a preview is produced:** protected workers were not verified. This is the safe expected fallback.
- **No full dry-run is offered:** there is no existing applicable group, and group creation was not separately
  authorized.
- **Recall returns `NOT FOUND` after capture:** confirm the actual write receipt, stored status, and effective
  recall filters before attributing the result to proposed-lifecycle filtering. Until then, the recall gap
  remains unverified; do not promote records merely to make the check pass.
- **A rerun proposes duplicates:** stop before capture and re-check repository identity, scope, provenance,
  and comparison coverage.
- **A source conflict appears:** keep both positions and ask which authority or lifecycle applies.

See the [behavioral test guide](tests/README.md) for the synthetic preview scenarios used to evaluate the
skill. Those fixtures illustrate behavior; they are not evidence about a real or private project.
