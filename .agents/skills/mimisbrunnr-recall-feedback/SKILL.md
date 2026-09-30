---
name: mimisbrunnr-recall-feedback
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
`CONTEXT_MEMORY_WRITE_TOKEN` for reset (write includes read). The **values** are runtime-only — never
written to a file, prompt or commit. Each request carries the token as a `Bearer` Authorization header.
A missing or wrong token returns `403`.

## Queries

Run the guard first (below, under **Smoke check**) — the three commands below are the only things
that carry a token, and the guard is what decides whether the token is allowed to leave. Each command
is written to be self-guarding rather than trusting the operator to have run the check: it re-asserts
the resolved host through the same helper and refuses rather than sending, so a copied-out command
block cannot skip the guard that documents it.

### 1. Which memories have never been recalled?

```bash
recall_feedback_curl GET "/api/context/recall-feedback/never-recalled?asOf=2026-09-18&limit=500" \
  "$CONTEXT_MEMORY_READ_TOKEN"
```

`asOf` is required (ISO 8601). The list already excludes memories captured within the recency grace
(it distinguishes "never recalled" from "recently captured and not yet retrieved"), so a new memory does
not pollute the signal.

### 2. How often does retrieval return nothing?

```bash
recall_feedback_curl GET "/api/context/recall-feedback/miss-rate?from=2026-09-11&to=2026-09-18" \
  "$CONTEXT_MEMORY_READ_TOKEN"
```

Returns `{ "retrievals": N, "misses": M, "missRate": 0.0 }`. Count a window before and after a tuning
change to show whether a change moved it — this is the before/after comparison NFR-03 exists for.

### 3. Reset the baseline

```bash
recall_feedback_curl POST "/api/context/recall-feedback/reset" "$CONTEXT_MEMORY_WRITE_TOKEN"
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

**Load it once per shell.** It defines `recall_feedback_curl`, the only function that sends a token, and
`recall_feedback_guard`, the origin check both it and the operator can call directly.

```bash
# ${VAR:-default} supplies loopback when unset. The base URL is parsed and its **resolved host**
# asserted to be loopback — matching the raw string with a glob instead approves
# `http://localhost:5141@192.0.2.1/` (the loopback prefix, non-loopback host via userinfo), which is
# exactly the bypass this guard exists to close. Mirrors context_memory_client.base_url().
#
# The check runs in a *function*, not a `( ... )` subshell: a subshell's `exit 1` returns to the
# caller with a non-zero status nobody tested, so the three requests that followed it ran regardless.
# Here the guard is the last statement before the token is read, so a refusal cannot be followed by a
# request inside the same function.
recall_feedback_guard() {
  local base="${CONTEXT_MEMORY_BASE_URL:-http://localhost:5141}"
  python3 - "$base" <<'PY'
import sys, urllib.parse
url = sys.argv[1]
parsed = urllib.parse.urlparse(url)
# Report only parsed, non-secret parts. The raw url is never echoed: the branch below is reached
# precisely when it carries userinfo, so printing it would write a credential to the terminal and
# into any agent or CI transcript — in a guard whose purpose is to stop the token crossing.
if parsed.scheme not in ("http", "https") or parsed.username or parsed.password \
        or parsed.path not in ("", "/") or parsed.query or parsed.fragment:
    print("refusing CONTEXT_MEMORY_BASE_URL: must be a bare HTTP(S) origin with no "
          f"credentials, path, query or fragment (got scheme={parsed.scheme!r}, "
          f"userinfo={'present' if parsed.username or parsed.password else 'absent'}, "
          f"path={parsed.path!r})", file=sys.stderr)
    sys.exit(1)
if parsed.hostname not in ("localhost", "127.0.0.1", "::1"):
    print(f"refusing non-loopback CONTEXT_MEMORY_BASE_URL host: {parsed.hostname}", file=sys.stderr)
    sys.exit(1)
PY
}

# The token arrives as a *function argument*, not as a curl `-H` argument. Anything in a function's
# argument list is visible in `ps` to every user on the host for the life of the process, so
# `-H "Authorization: Bearer $TOKEN"` puts the credential in the process table and in any shell
# trace. curl reads the header from a file descriptor instead, so the value never becomes an argv
# element of any process.
recall_feedback_curl() {
  local method="$1" path="$2" token="$3"
  if ! recall_feedback_guard; then
    echo "recall-feedback: origin refused; no request was sent." >&2
    return 1
  fi
  if [ -z "$token" ]; then
    echo "recall-feedback: no token supplied; no request was sent." >&2
    return 1
  fi
  # Written to a mode-600 temp file rather than a pipe, so the header value never passes through a
  # shell variable expansion visible to another process, and the file is unlinked immediately.
  local header
  header="$(mktemp)" || return 1
  chmod 600 "$header"
  printf 'Authorization: Bearer %s\n' "$token" >"$header"
  # --noproxy '*' is not optional: curl honours http_proxy/ALL_PROXY from the environment, and a
  # proxy set for outbound traffic would otherwise receive a loopback request *and* its header.
  # --fail-with-body keeps a 4xx body visible instead of collapsing a refusal to an empty success.
  local status=0
  curl -sS --noproxy '*' --fail-with-body \
    -X "$method" -H "@$header" \
    "${CONTEXT_MEMORY_BASE_URL:-http://localhost:5141}${path}" || status=$?
  rm -f "$header"
  unset token header
  return "$status"
}
```

`--fail-with-body` (curl ≥ 7.76) turns a `403` into a non-zero exit instead of a silent success, so a
wrong token cannot read as an empty result set. On older curl, drop that one flag — the loopback
refusal and the argv redaction are the parts that protect the token.

**A note on what this does not prove.** The guard approves an *origin*; it does not make the response
safe, and it does not audit the store. It exists so a misconfigured `CONTEXT_MEMORY_BASE_URL` cannot
carry a read or write token to a host the operator did not intend, which is the same exfiltration
class the credential-bearing redirect fix closed on the client side.
