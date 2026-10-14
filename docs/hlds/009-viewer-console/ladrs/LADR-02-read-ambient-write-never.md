# LADR-02 — Browser holds no token; Caddy injects the read token

## Context

The API enforces a capability split (`ApiAccessAuthorizer`): read token and write token, hashed,
checked against the `Authorization` header. Read is ambient; write must never be ambient.

## Decision

The browser never holds a credential. Caddy injects the **read token** into the `/api` reverse-proxy. The
**write token** lives server-side in the bridge and is exercised only on an explicit human approve of a
capture. The SPA is served unauthenticated; only `/api` proxy calls carry the injected read token.

## Consequences

- Read is ambient as designed; write is never ambient.
- A captured write token is never in browser storage, a committed artifact, or a log line.
- The SPA cannot reach the Host directly; it must go through Caddy, so the token is not exposed.

## Status

Draft
