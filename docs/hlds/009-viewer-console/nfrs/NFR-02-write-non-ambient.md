# NFR-02 — Write non-ambient

| Attribute | Boundary |
|---|---|
| **Attribute** | Security / credential handling |
| **Target** | The browser never holds a write token; no write without an explicit approve |
| **Status** | Draft |

The write token is server-side only (the bridge). The SPA carries no credential. A capture is proposed
read-only; the write token is exercised only on an explicit human approve.

**Acceptance:** with no user action, no write-capable request is issued; the browser's storage and the
SPA source contain no credential; an approve is the sole trigger for a write-capable call.
