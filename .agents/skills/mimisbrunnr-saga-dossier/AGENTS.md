# AGENTS.md - mimisbrunnr-saga-dossier

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — preview, bundle, compose, focus, findings and the reconciliation rules live there.

## Non-Negotiables

- **Env vars and who receives them.** The composer reads `CONTEXT_MEMORY_READ_TOKEN` and
  `CONTEXT_MEMORY_BASE_URL` from the environment only — nothing is loaded from a file, and never a
  write token. Only its read-only transport sends the token, as the `Authorization` header of the
  `preview` and `bundle` requests.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-22 | **Read token and base URL are environment-only (APPHOST LADR-007).** The shared client no longer seeds them from the machine credential file; the missing-credential message names the variable and where `run.sh` puts it. The file-seeding case became one asserting that importing the client loads nothing from a file carrying every client name. | LADR-007 |
| 2026-10-16 | The base-URL default comes from the shared client (`client.DEFAULT_BASE_URL`) instead of a re-stated `http://localhost:5141` literal — the env var name was already borrowed from it, so the port was the one value that could drift away from every other client's fallback | — |
| 2026-10-15 | Ralph loop: `version: true` is refused as the near-miss path already refuses it; a non-object bundle answer is a refusal; five dead symbols removed | — |
| 2026-09-23 | Composer, invocation contract and L0 harness implemented | — |
| 2026-09-24 | NFR-05 attribution test can no longer pass vacuously | #99 |
| 2026-09-26 | README rewritten for humans | — |
| 2026-09-26 | README quickstart creates the gitignored output folder first | — |
| 2026-09-26 | Read token refused off loopback and on redirects | — |
| 2026-09-27 | `SKILL.md` gained standard frontmatter | — |
| 2026-09-27 | Credential-transport guards covered by tests, including `localhost.evil.example` lookalikes | — |
| 2026-09-27 | Loopback guard checks userinfo, path, query and fragment, not just scheme + host | #118 |
| 2026-09-30 | History bundles compose | — |
| 2026-10-01 | An unparseable base URL is refused with fixed text | — |
| 2026-10-01 | No `ghost` category exists | — |
| 2026-10-02 | Renamed to `mimisbrunnr-saga-dossier` | #161 |
| 2026-10-03 | `bundle` reads token + base URL from the machine credential (never the write token) | #168 |
| 2026-10-03 | Heimdallr is skipped when all anchors are bound | #168 |
| 2026-10-03 | `bundle` anchor flags merge into `--body` (explicit wins) | #168 |
| 2026-10-04 | Bundle refuses either spelling of the write token, not one | #174 |
| 2026-10-04 | Dead `--base-url` env write removed | #174 |
| 2026-10-04 | A truncated error body (`IncompleteRead`) no longer escapes `main()` | #174 |
| 2026-10-05 | Heimdallr-withheld or unredactable tickets are disclosed, not read as "no branch ticket" | #182 |
| 2026-10-05 | `--judgements` refuses an evidence-free `near-miss-tag` (LADR-10) | #182 |
| 2026-10-05 | The CLI dropped the agent's judgements, so no dossier could carry a gap or… | #182 |
| 2026-10-05 | An unparseable `--asof` is refused | #179 |
| 2026-10-06 | A consolidated claim shows a differing origin's words (`also stated as`) | — |
| 2026-10-06 | A contradiction needs two distinct uuids (two versions of one memory are supersession) | #190 |
| 2026-10-06 | Body-only and secondary-origin conditions are kept and cited | #190 |
| 2026-10-06 | Provenance-cycle break point is rendered | #188 |
| 2026-10-06 | Near-miss findings keep the helper's evidence qualifications through composition and rendering | #186 |
| 2026-10-06 | A repeated omission let an open reconciliation exit 0 | #184 |
| 2026-10-07 | Approval compares `selection` and `selectedCount` | — |
| 2026-10-07 | Bundle guard reuses odin's `write_tokens_present` (a two-name list let `Parameters__api-write-token` and case variants through) | — |
| 2026-10-07 | `SKILL.md` drops a stale harness test count | #200 |
| 2026-10-07 | Dedupe on the whole normalized finding, not category+memories+basis, so findings differing only in scope/classification/observation/qualification… | — |
| 2026-10-14 | Every shared dependency is reached through the plugin's shared script folder (HLD-010 B2 /… | #222 |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
