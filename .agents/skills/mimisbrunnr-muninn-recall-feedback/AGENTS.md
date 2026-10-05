# mimisbrunnr-muninn-recall-feedback — AGENTS.md

## TL;DR

Thin, read-mostly helper over the three HLD-004 NFR-03 tuning surfaces: never-recalled list, miss rate
over a window, and baseline reset. Calls the Host API — no direct database access, no raw feedback
records. Output is identity, count and time only.

## Non-Negotiables

- **The loopback guard gates the request; it does not merely report.** The check is a shell
  **function**, not a `( ... )` subshell, and it is the last statement before the token is read. A
  subshell's `exit 1` returns to a caller that never tested it, so the three `curl` commands that
  followed it ran regardless of what the guard decided — the guard was decorative. If you refactor the
  smoke check, keep the refusal and the send on one code path.
- **The token is never an argv element.** `recall_feedback_curl` reads the `Authorization` header
  from a mode-600 temp file (`-H @file`) and the operator passes the token as a function argument,
  never as a `curl -H "Authorization: Bearer …"` literal. Anything in a function's argument list is
  readable in `ps` by every user on the host.
- **The guard lives in `scripts/recall_feedback.sh`, and `SKILL.md` sources it.** Do not paste a copy
  of the functions back into `SKILL.md`: the harness tests the script, so an inline copy is untested
  code that the operator runs instead.
- **The request path is validated, not just the origin.** The path is concatenated after an approved
  origin, so `@evil.example/x` would make `localhost:5141` userinfo and send the token to
  `evil.example`. Only an absolute path with no `@`, `\`, whitespace, control character or leading
  `//` is sent.
- **The base URL never reaches argv.** It is handed to the python parser through an environment
  variable, because a refused base is refused precisely when it may carry userinfo.
- **`--noproxy '*'` is not optional.** `curl` honours `http_proxy`/`ALL_PROXY` from the environment, so
  a proxy set for outbound traffic would receive a *loopback* request and its header with it. The guard
  approves an origin; it does not neutralise the transport.
- **`-q` is the first argument of every `curl` call.** curl reads `~/.curlrc` unless `-q` opens the
  command line, and a default config can add a header, a proxy or `--location` — so the guard would
  approve one request and curl would send another. It must be first: curl honours `-q` only there.
- **The query window is the operator's.** `SKILL.md`'s queries read `RF_FROM`/`RF_TO` through
  `${VAR:?}`, with `asOf` = `RF_TO`, and carry no literal date: a copied fixed window re-measures the
  same past period on every run, so a tuning change could never show in the before/after comparison.
- **No free-text query goes to these endpoints.** They are tuning surfaces, not a retrieval path, and
  feedback must never influence ranking.
- **The output is identity and counts.** Do not attempt to reconstruct query text or memory content
  from it; the endpoints do not return either.

## Key Behaviors

- **`--fail-with-body` turns a `403` into a non-zero exit** rather than an empty success, so a wrong
  token cannot read as "no findings", and keeps the refusal's body (plain `--fail` drops it). Needs curl ≥ 7.76; on older curl drop that one flag — the
  loopback refusal and the argv redaction are the parts that protect the token.
- **A missing token is refused before the request**, not sent and 403'd.
- **The temp header file is unlinked by an `EXIT` trap in the send subshell**, on success, failure and
  `HUP`/`INT`/`TERM` alike. The subshell only scopes the traps — every refusal has already returned
  from the calling function before it starts.
- **`verify`/`reset` need different capabilities**: the two read queries take
  `CONTEXT_MEMORY_READ_TOKEN`, reset takes `CONTEXT_MEMORY_WRITE_TOKEN` (which includes read). Values
  are runtime-only and are never written to a file, prompt or commit.
- **A non-loopback base URL is refused, not retargeted.** The base is *parsed* and its resolved host
  asserted, never glob-matched as a string: `http://localhost:5141@192.0.2.1/` has a loopback prefix
  and a non-loopback host, which is the exact bypass the guard exists to close. Mirrors
  `context_memory_client.base_url()`.
- **A refused base reports each part as present/absent, never its value.** Userinfo, path, query and
  fragment are all named that way; the path was once quoted (`path={!r}`), and a token pasted into the
  base lands in the path or query as readily as in the userinfo.
- **A base the parser cannot read is refused with a fixed message.** `urlsplit` raises `ValueError`
  for an NFKC-confusable netloc character, a non-numeric port or an unbalanced IPv6 bracket, and the
  message quotes the netloc — userinfo included. The guard's stderr is the caller's stderr, so that
  error must never escape the `try`; the sibling composer closes the same hole at
  `dossier_composer.py:1113-1117`.

## Test References

- `python3 -B .agents/skills/mimisbrunnr-muninn-recall-feedback/tests/run_tests.py` — stdlib unittest, no
  network. Sources `scripts/recall_feedback.sh` under bash with a recording fake `curl` and a `python3`
  shim on `PATH`, and asserts: non-loopback, userinfo and path-bearing origins are refused before any
  request (and neither the userinfo nor a path, query or fragment is echoed — each is reported
  present/absent); host-moving paths (`@host/…`, `//host`, a scheme, whitespace,
  `\`) are refused; an accepted request carries `--noproxy '*'`, sends the token only from a mode-600
  header file and never on argv; the header file is gone after success, curl failure and `TERM`; the
  base URL never appears in any python argv; every `curl` call opens with `-q` (the fake curl refuses a
  call that does not, a static scan covers every invocation in the script, and a real-curl case with an
  isolated `HOME` proves a `.curlrc` canary is ignored, skipped when curl is absent); `--fail-with-body`
  reaches curl, and a real curl against an in-process loopback responder answering `403` exits 22 with
  the body kept (skipped when curl lacks the option); and `SKILL.md` sources the script instead of an
  inline copy, its two queries carry no literal date, refuse to send with `RF_FROM`/`RF_TO` unset and
  send the operator's window when set. Runs on macOS bash 3.2 and GNU bash.
- **CI runs this harness, and a red harness is a gate failure.** `.github/workflows/pr-gate.yml`'s
  `python-harnesses` job ("Test recall-feedback skill") runs it on both the 3.9 and 3.12 legs.
  Run it locally whenever the script changes — the gate runs the same command.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-05 | **Documented queries take the operator's window, and the harness can see a lost `--fail-with-body`.** `SKILL.md` hard-coded a September 2026 window and `asOf`, so following it re-measured the same past period on every run and a tuning change could never appear in the before/after comparison; the queries now read `RF_FROM`/`RF_TO` (`asOf` = `RF_TO`) and stop before sending when either is unset. The fake curl returned success whatever the flags, so deleting `--fail-with-body` left the harness green while a `403` read as an empty result; the harness now asserts the option reaches curl and drives a real curl against a loopback `403` responder (exit 22, body kept). Harness 17 -> 21. | issue 179 |
| 2026-10-05 | **Two request-integrity leaks in the guard closed.** (1) A refused base printed `path={!r}`, so a credential pasted into the base URL's path reached stderr — and the guard's stderr is the caller's transcript; the refusal now reports path, query and fragment as present/absent, the way userinfo already was. (2) curl ran without `-q`, so a default `~/.curlrc` could add a header, a proxy or a redirect-follow to a request the guard had approved; `-q` is now curl's first argument. The fake curl refuses any call without it, a static check covers every invocation, and a real-curl case with an isolated `HOME` proves a `.curlrc` is ignored. | issue 179 |
| 2026-10-02 | Renamed `mimisbrunnr-recall-feedback` → `mimisbrunnr-muninn-recall-feedback` (folder, `name:`, CI paths, every cross-reference). Muninn is Odin's raven Memory — reports what was never seen. Behaviour unchanged; harness green. | session request |
| 2026-10-01 | The guard refuses a base URL the parser cannot read (NFKC-confusable netloc character, non-numeric port, unbalanced IPv6 bracket) with a fixed message, and hoists `parsed.hostname` inside the same `try`. `urlsplit`'s `ValueError` quotes the netloc, userinfo included, and the guard's stderr is the caller's — so the uncaught traceback echoed the credential this script exists to withhold, the same leak closed in the sibling composer. The existing userinfo test covers only a well-formed origin, so it passed throughout. | HLD-004 NFR-03 |
| 2026-10-01 | The stale hedge that this harness is "not yet in CI" is gone: `pr-gate.yml`'s `python-harnesses` job already runs it on the 3.9 and 3.12 legs, and the bullet now names that job rather than inviting a maintainer to weaken a step that is load-bearing. | PR review (Low) |
| 2026-10-01 | **Guard extracted to `scripts/recall_feedback.sh` with a committed harness (`tests/run_tests.py`).** Two residual exfiltration paths closed: the request path was concatenated unvalidated after the approved origin, so `@evil.example/x` sent the bearer token off-box — it must now be absolute with no `@`, `\`, whitespace, control character or leading `//`; and the base URL reached python as argv (readable in `ps`, may carry userinfo) — it now travels in an environment variable. The header file is unlinked by an `EXIT` trap that also fires on `HUP`/`INT`/`TERM`. | HLD-004 NFR-03 |
| 2026-09-29 | **Created — this skill was the only one in the set without an `AGENTS.md`.** It also records the B2 fix: the loopback guard ran in a detached `( ... )` subshell, so its `exit 1` gated nothing and the three requests that followed it ran regardless; the check is now a function so a refusal is the last statement before the token is read, the curls gained `--noproxy '*'`, and the token moved out of argv into a mode-600 header file. Both re-opened, for the skill package, the credential-exposing class that the redirect fix closed on the client side. | HLD-004 NFR-03 |
