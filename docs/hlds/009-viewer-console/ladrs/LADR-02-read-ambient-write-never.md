# LADR-02 — Credential-gated Console; the Host is the capability authority

## Context

The API enforces a capability split (`ApiAccessAuthorizer`): a read token and a write token, hashed and
compared in constant time against the `Authorization` header. A write token also satisfies a read
requirement, so "which token" and "which capability" are one question with three answers: read-only,
read+write, or refused.

The Console needs both tiers at once — a practitioner browsing the corpus and a reviewer approving
captures — without the image holding a credential, and without a read-only session being able to write.

## Decision

The Console is **credential-gated**. The practitioner supplies a `CONTEXT_TOKEN` at login; the SPA holds
it for the session (`sessionStorage`, dropped with the tab) and sends it as a Bearer header on every
call. Caddy and both bridges **forward** that header to the Host, which resolves the capability and
enforces it — the Host is the authority, not the UI.

No token is **ambient**: neither the image nor any process environment carries one. Caddy no longer
injects a read token, and P4b starts without a write-token env var, taking the credential per request
and injecting it into the capture subprocess env only.

The tier is reported to the session by `GET /api/context/capabilities` (read-gated; the handler resolves
which token was presented), so the Console can enable the write surfaces — Approve/Reject — only for a
read+write session. A read-only token can never write, and the UI's gating is a convenience on top of
that, not the guarantee.

## Consequences

- **Supersedes the earlier "browser holds no token; Caddy injects the read token" model.** Read is no
  longer ambient: the practitioner presents a credential, and an unauthenticated request is 403.
- The write token is now present in a browser session for a write-capable operator. Mitigations: a
  strict `Content-Security-Policy` on the served SPA (`script-src 'self'`, `style-src 'self'` with
  `style-src-attr 'unsafe-inline'`), `esc()` on every echoed value, no `localStorage` persistence, and
  the loopback-only published port (NFR-01).
- NFR-02 narrows from "the write token is physically absent from the pod" to "no token is ambient in the
  pod; a per-request credential is the only source". Process isolation between P4a and P4b still holds.
- A captured token is never written to a log line, a committed artifact, or a span attribute (NFR-04).

## Status

Accepted — supersedes the read-injection form of this decision (2026-10-16).
