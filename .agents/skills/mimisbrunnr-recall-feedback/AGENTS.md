# mimisbrunnr-recall-feedback — AGENTS.md

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
- **`--noproxy '*'` is not optional.** `curl` honours `http_proxy`/`ALL_PROXY` from the environment, so
  a proxy set for outbound traffic would receive a *loopback* request and its header with it. The guard
  approves an origin; it does not neutralise the transport.
- **No free-text query goes to these endpoints.** They are tuning surfaces, not a retrieval path, and
  feedback must never influence ranking.
- **The output is identity and counts.** Do not attempt to reconstruct query text or memory content
  from it; the endpoints do not return either.

## Key Behaviors

- **`--fail-with-body` turns a `403` into a non-zero exit** rather than an empty success, so a wrong
  token cannot read as "no findings". Needs curl ≥ 7.76; on older curl drop that one flag — the
  loopback refusal and the argv redaction are the parts that protect the token.
- **A missing token is refused before the request**, not sent and 403'd.
- **The temp header file is unlinked in the same function**, on both the success and failure path.
- **`verify`/`reset` need different capabilities**: the two read queries take
  `CONTEXT_MEMORY_READ_TOKEN`, reset takes `CONTEXT_MEMORY_WRITE_TOKEN` (which includes read). Values
  are runtime-only and are never written to a file, prompt or commit.
- **A non-loopback base URL is refused, not retargeted.** The base is *parsed* and its resolved host
  asserted, never glob-matched as a string: `http://localhost:5141@192.0.2.1/` has a loopback prefix
  and a non-loopback host, which is the exact bypass the guard exists to close. Mirrors
  `context_memory_client.base_url()`.

## Test References

- **No committed harness.** The guard's behaviour is a shell function, so it is not covered by the
  skill's Python harnesses. When you change it, exercise the function directly: source the block from
  `SKILL.md`, then assert that a non-loopback host, a `userinfo` origin, a path-bearing origin and an
  empty token each return non-zero **and** that the request function is not reached. A subshell
  refactor is the regression this would catch.
- CI runs the two sibling harnesses (`mimisbrunnr-context-memory`, `mimisbrunnr-understanding`) and
  the npm smoke; none of them reach this skill.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-29 | **Created — this skill was the only one in the set without an `AGENTS.md`.** It also records the B2 fix: the loopback guard ran in a detached `( ... )` subshell, so its `exit 1` gated nothing and the three requests that followed it ran regardless; the check is now a function so a refusal is the last statement before the token is read, the curls gained `--noproxy '*'`, and the token moved out of argv into a mode-600 header file. Both re-opened, for the skill package, the credential-exposing class that the redirect fix closed on the client side. | HLD-004 NFR-03 |
