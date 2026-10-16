# Where Mímisbrunnr Loses

Stated, not apologised for. Each is a property of the design, and each is a thing to check before
relying on the well for it.

- **One machine, one practitioner.** Today's default deployment posture is a local Aspire stack —
  Postgres+AGE, MinIO and Seq on one host, reached over loopback. There is no multi-tenant isolation, no
  per-user identity, and no horizontal scale-out. Scope is enforced as a *filter on retrieval*, not as a
  security boundary between users. A non-loopback posture is documented
  ([`docs/wiki/docker.md`](docker.md)) but the target deployment posture is one of the open
  owner decisions below, not a settled property.
- **No real-time multi-agent coordination.** Two agents do not converse through the store. Capture is an
  explicit end-of-task checkpoint, so a fact held by one agent is invisible to another until someone
  finalizes. There is no queue, no lock-across-agents protocol and no partial-progress write.
- **The write path's judgement lives in the skill, so a weaker model degrades capture.** Semantic
  deduplication, atomicity scoring, link derivation and summary generation are model decisions the
  database cannot express as constraints. The mechanical half fails closed; the judgement half degrades
  quietly. A weaker model produces more compound records and coarser labels, not an error.
- **Redaction is a gate on recognisable shapes.** `set` refuses to write when the scrubber cannot run, so
  the gate never fails open — but the rules are fixed-shape fingerprints (`AKIA…`, `gh[pousr]_…`, PEM
  blocks, `secret=`/`token:`/`password=` assignments). A bare high-entropy value, an unlabelled blob, an
  unusual vendor token format or a password in prose passes through.
- **Scope is declared, not discovered.** Programme versus product knowledge is set on the group by the
  caller and enforced on read (the stored value is `program`). A capture under the wrong scope dimension
  is stored correctly and retrieved wrongly; nothing infers the right dimension for you.
- **A cross-group duplicate is invisible to the store.** A memory's identity is `(group, uuid)`, chosen so
  the write path needs no cross-group lock ordering. The same subject captured under a second group is a
  separate memory, not a version — the store neither detects nor merges it.
- **Recall is lexeme-AND, not semantic.** `/query` free text matches every lexeme under the `english`
  configuration, so a natural-language question defeats it. Structured facets, tags, kind and traversal
  are the reliable paths in; free prose is not.
- **Nothing is deletable on the write path.** Content addressing makes a blob immutable and unrefcounted.
  A superseded body is orphaned, never edited or removed — which is what makes a redaction a one-way
  operation, by design.
- **What "done" means is not written down, and six decisions are still open.** No tracked document defines
  MVP, so a completion asserted against a gitignored worktask is currently the only record. Six owner
  decisions — scope record, deployment posture, the redaction-gate question, untracked capture identity,
  the dossier collapse rule, and whether a design may be accepted over draft children — gate the rest of the
  backlog. Everything behind them is written down and waiting; none of it is guessed.
