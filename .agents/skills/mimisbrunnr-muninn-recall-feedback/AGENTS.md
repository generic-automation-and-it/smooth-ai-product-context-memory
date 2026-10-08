# mimisbrunnr-muninn-recall-feedback — AGENTS.md

> **References.** Every HLD, LADR, NFR, BRD, issue and PR number in this file belongs to the upstream
> repository, `generic-automation-and-it/smooth-ai-product-context-memory` (`docs/hlds/`, `docs/brd/`),
> not to a repository this skill is vendored into.

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
- **The token is never an argv element and never a file.** `recall_feedback_curl` pipes the
  `Authorization` header from the `printf` builtin into curl's stdin (`-H @-`). The operator passes
  only a command and dates; each command picks its capability (`read`/`write`) internally and the
  helper reads the matching token from the exported environment itself — never an argument, never a
  `curl -H "Authorization: Bearer …"` literal. A process's argv
  is readable in `ps` by every user on the host, and a temp header file (the previous design) put the
  token on disk where a kill left it — contradicting the "never written to a file" guarantee below.
- **`scripts/recall_feedback.sh` is the one entry point, and `SKILL.md` runs it.** It is executable,
  with one command per question (`never-recalled --to DATE [--limit N]`, `miss-rate --from DATE --to
  DATE`, `reset`, `guard`). Each command builds its own request path and picks its own capability, so
  the operator passes dates, never a path, a capability or a token. Do not paste a copy of the functions
  or a hand-built path back into `SKILL.md`: the harness tests the script, so an inline copy is untested
  code that the operator runs instead. Sourcing still defines the functions and runs nothing.
- **The tokens must be exported.** The script is its own process, so a token set as an unexported
  shell variable is invisible to it and the request is refused as tokenless, never sent.
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
- **The query window is the operator's.** `--from`/`--to` are required, with `asOf` = `--to`, and
  `SKILL.md` shows only the `YYYY-MM-DD` placeholder, never a literal date: a copied fixed window
  re-measures the same past period on every run, so a tuning change could never show in the
  before/after comparison. A missing, empty or malformed date (only `YYYY-MM-DD` or a `…Z` UTC
  instant), an unknown option, or an option the command does not take exits 2 before anything is sent,
  and the refusal never echoes the value — a pasted secret in the wrong slot stays off the terminal.
- **No free-text query goes to these endpoints.** They are tuning surfaces, not a retrieval path, and
  feedback must never influence ranking.
- **The output is identity and counts.** Do not attempt to reconstruct query text or memory content
  from it; the endpoints do not return either.

## Key Behaviors

- **`--fail-with-body` turns a `403` into a non-zero exit** rather than an empty success, so a wrong
  token cannot read as "no findings", and keeps the refusal's body (plain `--fail` drops it). Needs
  curl ≥ 7.76. On an older curl the fallback is `--fail`, never removing the option: without either a
  `403` exits 0 and a wrong token reads as an empty result (consumer review 5438563690 #8).
- **A missing token is refused before the request**, not sent and 403'd.
- **No header file exists to clean up.** The header travels through a pipe, so there is no temp file,
  no `EXIT` trap and no window in which a `SIGKILL` leaves the token on disk. Every refusal returns
  from the calling function before the pipe starts.
- **`verify`/`reset` need different capabilities**: the two read queries take
  `CONTEXT_MEMORY_READ_TOKEN`, reset takes `CONTEXT_MEMORY_WRITE_TOKEN` (which includes read). Values
  are runtime-only and are never written to a file, prompt or commit.
- **A non-loopback base URL is refused, not retargeted.** The base is *parsed* and its resolved host
  asserted, never glob-matched as a string: `http://localhost:5141@192.0.2.1/` has a loopback prefix
  and a non-loopback host, which is the exact bypass the guard exists to close. Mirrors
  `context_memory_client.base_url()`.
- **A refused base reports each part as present/absent, never its value.** Userinfo, path, `;params`,
  query and fragment are all named that way; the path was once quoted (`path={!r}`), and a token pasted
  into the base lands in the path or query as readily as in the userinfo. The scheme is reported only as
  `valid`/`invalid` and a non-loopback host is refused without naming it: `admin:hunter2` parses with
  scheme `admin`, and a token pasted into the wrong variable parses as a hostname. `params` must be
  checked separately because `urlparse` moves `;…` out of `path`, so `http://localhost:5141/;tok=x`
  otherwise reads as a bare origin.
- **A base the parser cannot read is refused with a fixed message.** `urlsplit` raises `ValueError`
  for an NFKC-confusable netloc character, a non-numeric port or an unbalanced IPv6 bracket, and the
  message quotes the netloc — userinfo included. The guard's stderr is the caller's stderr, so that
  error must never escape the `try`; the sibling composer closes the same hole at
  `dossier_composer.py` `_assert_loopback`.

## Test References

- `python3 -B .agents/skills/mimisbrunnr-muninn-recall-feedback/tests/run_tests.py` — stdlib unittest, no
  network. Sources and executes `scripts/recall_feedback.sh` under bash with a recording fake `curl` and
  a `python3` shim on `PATH`, and asserts: non-loopback, userinfo, path-bearing and `;params`-bearing origins are
  refused before any request (and neither the userinfo nor a path, params, query or fragment is echoed —
  each is reported present/absent — nor the scheme or a non-loopback hostname); host-moving paths (`@host/…`, `//host`, a scheme, whitespace,
  `\`) are refused; an accepted request carries `--noproxy '*'`, sends the token on curl's stdin (`-H @-`)
  and never on argv; `TMPDIR` is empty while curl runs (GET and POST), after a curl failure and on
  `TERM`; the
  base URL never appears in any python argv; every `curl` call opens with `-q` (the fake curl refuses a
  call that does not, a static scan covers every invocation in the script, and a real-curl case with an
  isolated `HOME` proves a `.curlrc` canary is ignored, skipped when curl is absent); `--fail-with-body`
  reaches curl, and a real curl against an in-process loopback responder answering `403` exits 22 with
  the body kept (skipped when curl lacks the option); each command sends its own method, path and
  capability's token (`reset` POSTs with the write token, the queries GET with the read token, and the
  other token alone is refused), every malformed or misplaced argument exits 2 with nothing sent or
  echoed, `guard` sends nothing, and sourcing runs nothing; and `SKILL.md` runs the executable script
  (all four commands documented, no `source`, no inline copy), its queries carry no literal date,
  refuse to send with the placeholder and send the operator's window when filled. Runs on macOS bash 3.2 and GNU bash.
- **CI runs this harness, and a red harness is a gate failure.** `.github/workflows/pr-gate.yml`'s
  `python-harnesses` job ("Test recall-feedback skill") runs it on both the 3.9 and 3.12 legs.
  Run it locally whenever the script changes — the gate runs the same command.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-07 | Token storage between shells is operator machine config (provisioned env file, managed `~/.zshrc` block); the skill writes no value anywhere. | review 5440964552 |
| 2026-10-07 | `SKILL.md` lists `::1` among accepted loopback hosts. | issue 200 |
| 2026-10-07 | Dates checked against the calendar, not just the shape (`2030-02-30` refused); older-curl fallback is `--fail`, never dropping it — without one a 403 exits 0. | review 5438563690 |
| 2026-10-07 | One executable script with subcommands replaces sourcing plus hand-built `recall_feedback_curl` lines; each command picks its own path and token. Tokens must now be exported. | PR 196 |
| 2026-10-06 | Call sites pass a capability (`read`/`write`), never a token; the helper reads the token from the environment. | issue 190, review 5430979214 |
| 2026-10-06 | `urlparse` reads `host?` as an empty query, so bare `?`, `#`, `;` passed as an origin; the delimiters are refused. | issue 188 |
| 2026-10-06 | Header file replaced by stdin (`-H @-`): a mode-600 temp file still left the token on disk after `SIGKILL`. | issue 184 |
| 2026-10-05 | `;params` passed as a bare origin (`urlparse` moves them out of `path`); refusals no longer name scheme or host. | issue 182 |
| 2026-10-05 | Queries take the operator's window: a hard-coded date re-measures the same past period forever. | issue 179 |
| 2026-10-05 | Refusals echoed the base path (a pasted credential); now present/absent only. | issue 179 |
| 2026-10-02 | Renamed to `mimisbrunnr-muninn-recall-feedback`. | session request |
| 2026-10-01 | An unparseable base URL is refused with fixed text: `urlsplit`'s `ValueError` quotes userinfo into stderr. | HLD-004 NFR-03 |
| 2026-10-01 | Harness runs in `pr-gate.yml` on 3.9 and 3.12; it is a gate, not optional. | PR review |
| 2026-10-01 | Guard extracted to a script with a harness; the request path is validated, since `@evil.example/x` after an approved origin moves the host. | HLD-004 NFR-03 |
| 2026-09-29 | Created. Guard ran in a `( ... )` subshell whose `exit 1` gated nothing; it is a function. | HLD-004 NFR-03 |
