---
name: mimisbrunnr-muninn-recall-feedback
description: Run the three recall-feedback tuning queries against the context-memory store — never-recalled list, miss rate over a window, and baseline reset. Use when a practitioner needs to see which memories are never recalled, measure how often retrieval returns nothing, or reset the feedback baseline before a tuning experiment. Read-only insight into retrieval health. Triggers on "never recalled", "miss rate", "recall feedback", "which memories are never returned".
effort: medium  # three fixed API queries plus interpreting the result
---

# Recall Feedback

Thin helper for the three HLD-004 NFR-03 tuning questions. Calls the Host API — no direct database
access, no raw feedback records. Output is identity / count / time only; content and query text never
leave the store.

Base URL: `CONTEXT_MEMORY_BASE_URL` (default `http://localhost:5141`). Loopback origins only — the
client refuses any origin that is not localhost/127.0.0.1, so a value like `https://api.example.com`
fails before a request is sent. Export it once, or `set -a && source .context/mimisbrunnr.env && set +a`
if you provisioned via `scripts/provision-credentials.sh`.

API access requires runtime credentials: `CONTEXT_MEMORY_READ_TOKEN` for the two read queries and
`CONTEXT_MEMORY_WRITE_TOKEN` for reset (write includes read). They must be **exported** (an
`export` in the shell or your `~/.zshrc`, or the `set -a` line above): the script runs as its own
process and sees only the environment. The **values** are runtime-only — never written to a file,
prompt or commit. Each request carries the token as a `Bearer` Authorization header. A missing token
is refused before the request; a wrong one returns `403`.

## Queries

Every command runs `scripts/recall_feedback.sh` (path relative to this skill's directory; from the
repository root, `.agents/skills/mimisbrunnr-muninn-recall-feedback/scripts/recall_feedback.sh`). The
script builds the request path and picks the token itself: you pass dates, never a path or a token.
Each command re-checks the base URL and refuses rather than sending, so no step depends on the smoke
check below having run first.

**Choose the window first.** The dates are the operator's, not the skill's — there is no default, and
a copied date silently re-measures an old window. Pass ISO 8601 dates (`YYYY-MM-DD`, or a UTC instant
ending in `Z`; a `+hh:mm` offset is refused). A missing, empty or malformed date, an unknown option, or
an option the command does not take stops the script before it sends (exit 2).

### 1. Which memories have never been recalled?

```bash
.agents/skills/mimisbrunnr-muninn-recall-feedback/scripts/recall_feedback.sh never-recalled --to YYYY-MM-DD
```

`--to` is the `asOf` and is the window's end, so the list describes the same period as the miss rate.
`--limit N` (1–10000, default 500) caps the list. The list already excludes memories captured within
the recency grace (it distinguishes "never recalled" from "recently captured and not yet retrieved"),
so a new memory does not pollute the signal.

### 2. How often does retrieval return nothing?

```bash
.agents/skills/mimisbrunnr-muninn-recall-feedback/scripts/recall_feedback.sh miss-rate --from YYYY-MM-DD --to YYYY-MM-DD
```

Returns `{ "retrievals": N, "misses": M, "missRate": 0.0 }`. Count a window before and after a tuning
change to show whether a change moved it — this is the before/after comparison NFR-03 exists for. Run
the queries once with `--from`/`--to` set to the window ending at the change, then again with an
equal-length window starting at it; record both pairs of dates beside the results, since the numbers
mean nothing without them.

### 3. Reset the baseline

```bash
.agents/skills/mimisbrunnr-muninn-recall-feedback/scripts/recall_feedback.sh reset
```

Legitimate, not destructive: feedback is disposable (LADR-04), and a tuning experiment must be able to
start from a clean baseline. Returns the number of records deleted.

## Posture

- These are **practitioner tuning** surfaces, consumed occasionally. Not a monitoring product — no
  dashboards, no alerting, no polling.
- Never call them from the retrieval path; feedback must not influence ranking.
- The output is identity and counts. Do not attempt to reconstruct query text or memory content from it.

## Smoke check

A non-loopback base URL means the token would cross a network. The guard **stops the request** rather
than reporting a problem and letting the next line run anyway.

`guard` checks `CONTEXT_MEMORY_BASE_URL` and sends nothing. Inside the script,
`recall_feedback_curl` is the only function that sends a token, and every command above goes through
it. From the repository root:

```bash
.agents/skills/mimisbrunnr-muninn-recall-feedback/scripts/recall_feedback.sh guard
```

What the script enforces before any token leaves:

- The base URL is **parsed** and its resolved host asserted to be loopback; a raw-string glob would
  approve `http://localhost:5141@192.0.2.1/` (loopback prefix, non-loopback host via userinfo). A base
  carrying credentials, a path, `;params`, a query or a fragment is refused, and the refusal never
  echoes it — it reports only whether each part is present or absent, and whether the scheme is
  acceptable; a non-loopback host is refused without naming it.
- The base reaches the parser through the environment, not argv, so it is never visible in `ps`.
- The request path must be absolute and carry no `@`, `\`, whitespace or leading `//` — a path such as
  `@evil.example/x` appended to an approved origin would otherwise move the request's host.
- The token reaches curl on stdin (`-H @-`) from the shell's `printf` builtin: never an argv element and
  never a file, so nothing holding it is left on disk, even after a kill.
- `--noproxy '*'`: curl honours `http_proxy`/`ALL_PROXY`, and a proxy would otherwise receive the
  loopback request and its header.
- `-q` is curl's first argument, so a default `~/.curlrc` (an extra header, a proxy, `--location`)
  cannot change the request the guard approved.

The refusals are checked in the calling function, not a `( ... )` subshell whose `exit 1` nobody
tests, so a refusal cannot be followed by a request.

`--fail-with-body` (curl ≥ 7.76) turns a `403` into a non-zero exit instead of a silent success, so a
wrong token cannot read as an empty result set. On older curl, drop that one flag from the script —
the loopback refusal and the argv redaction are the parts that protect the token.

**A note on what this does not prove.** The guard approves an *origin*; it does not make the response
safe, and it does not audit the store. It exists so a misconfigured `CONTEXT_MEMORY_BASE_URL` cannot
carry a read or write token to a host the operator did not intend, which is the same exfiltration
class the credential-bearing redirect fix closed on the client side.
