# NFR-05 cost evidence — 2026-09-17

## Method

Run from repository root:

```bash
python3 -B .agents/skills/mimisbrunnr-odin-context-memory/tests/measure_cost.py
python3 -B .agents/skills/mimisbrunnr-odin-context-memory/tests/run_tests.py
```

The deterministic fixture models one checkpoint containing ten atomic facts and a saturated baseline
recall of 200 synthetic cheap rows. It estimates contract bounds and serialized main-context bytes; it
does not execute live agent or HTTP workflows.
It never reads a real memory or emits memory content. Provider invocation and token counts are not
available from repository execution and are not inferred from logical fact counts.

## Structural Estimates

| Workflow | Agent invocations | Logical summary judgements | Logical keyword judgements | HTTP calls / recall passes | Blob I/O | Main-context bytes |
|---|---:|---:|---:|---|---|---:|
| Normal ten-fact write | 1 write agent | 10 | 10 | minimum 3: preflight, baseline query, set | at most 20 blob store calls and 20 uploads; at most 62 object-store requests (248 HTTP attempts with retries) | digest only; provider measurement unavailable |
| Ten-fact dry-run | 1 write agent | 10 | 10 | same minimum 3 | 0 | digest only; provider measurement unavailable |
| Explicit deep search | 1 delegated agent | workflow-dependent | workflow-dependent | 1 baseline + at most 4 keyword + 5 traversal passes | 0 unless later selected drill-down | bounded conclusion only |
| Lookup | 1 read agent | n/a | n/a | bounded read | selected drill-down only | 3,030 synthetic bytes |
| Grounding | 1 read agent | n/a | n/a | bounded read | selected drill-down only | 14,784 synthetic bytes |
| Previous raw-row path | main session | n/a | n/a | baseline query | none by default | 107,401 synthetic bytes |

Deep-search tests cap candidate judgement at 400 unique UUID/version pairs. Default execution performs zero
optional deep-search passes. Dry-run and write use identical semantic stages; only `set` persistence and
blob writes differ. The write's blob bound has three levels, and the figure that matters depends on
which one is being budgeted (`measure_cost.py` reports all three):

- **Store calls: at most 20.** `SetMemories` makes one `IBlobStorage.StoreAsync` call per content-bearing
  item, before the transaction opens. An authority resolution won by the existing claim writes the losing
  candidate version and then restores the existing winner as a second content-bearing version, so one
  fact can put two such items in a `set`; ten facts reach the 20-item `set` limit. A divergence record
  carries no content and adds no call. Uploads are bounded by the same 20, and usually sit well below it:
  the restored winner's bytes normally already exist.
- **Object-store requests: at most 62.** One store call is not one request. `S3BlobStorage.StoreAsync`
  issues a `StatObject` existence check (1 request when the bytes exist); for new bytes it then checks
  the bucket and uploads (`StatObject`, `BucketExists`, `PutObject` — 3 requests). The first write into a
  missing bucket adds `MakeBucket`, and a `BucketExists` re-check when that create loses a race — 2 more,
  once per `set`. Twenty new bodies therefore cost 20 × 3 + 2 = 62 requests, 60 once the bucket exists.
- **HTTP attempts: at most 248.** The MinIO client runs on an `IHttpClientFactory` client under the
  Host's `AddStandardResilienceHandler`, whose default retry strategy allows 3 retries per request for
  every method, `PUT` included. A transient fault can therefore send each request up to four times
  (62 × 4), bounded in practice by the handler's 30-second total timeout.

The bound was first published as 0–10, one per fact, which missed the restoration write; it was corrected
to 0–20 "blob store operations" on 2026-10-05 (issue 182), which still counted store calls under the name
of operations and so understated what the object store sees by about a factor of three, before retries
(corrected 2026-10-06, issue 184).

Delegation reduces representative main-context payload by 97.2% for lookup and
86.2% for grounding versus the 200-row synthetic baseline. Aggregate token spend may increase because
delegated agents establish their own context.

## Unavailable Measurements And Blocker

- Provider invocation count is platform-dependent and unavailable to this repository harness.
- Input, output and cache token counts are unavailable without provider usage telemetry.
- Logical judgement counts do not imply provider invocation counts; one invocation may batch many facts.
- Real end-to-end agent token telemetry and one live delegated capture/recall remain the named blocker
  for accepting NFR-05. Status therefore remains Draft.

## Working-Tree API Verification

An isolated synthetic product group was created against working-tree AppHost on 2026-09-17. One
two-memory payload used fixed caller create UUIDs and one new-to-new link.

| Check | Observed |
|---|---|
| Dry-run | `created=2`, `linked=1`, both fixed UUIDs returned, no blob addresses |
| Write | Same counts and UUIDs as dry-run |
| Read-back | Read-token depth-one traversal returned exactly one path |
| Teardown | Supported `scripts/stop-dev-stack.sh`; persistent data volumes retained; temporary AppHost user secrets removed |

This proves API mechanics and capability use, not delegated model token usage. It therefore does not
close the blocker above.
