# Flow — View vs Approve

```mermaid
sequenceDiagram
    participant U as Practitioner
    participant SPA as Console SPA
    participant Caddy as Caddy
    participant Bridge as Bridge
    participant Host as Host API
    participant Capture as Capture pipeline

    %% ---- View path (read only) ----
    U->>SPA: open / (loopback)
    SPA->>Caddy: /api/context/query (filters)
    Caddy->>Host: /api/context/query + Read token
    Host-->>Caddy: CheapMemory[]
    Caddy-->>SPA: rows
    SPA->>Caddy: /api/context/memories/{uuid}/versions
    Caddy->>Host: versions (Read)
    Host-->>Caddy: version history
    Caddy-->>SPA: versions
    SPA->>Caddy: /api/context/memories/{uuid}/versions/{v}/blob
    Caddy->>Host: blob (Read, on demand)
    Host-->>Caddy: blob body
    Caddy-->>SPA: blob

    %% ---- Graph path (read only) ----
    SPA->>Caddy: /api/context/paths (bounded depth)
    Caddy->>Host: paths + Read token
    Host-->>Caddy: edges / nodes
    Caddy-->>SPA: graph

    %% ---- Approve path (write, gated) ----
    U->>SPA: view a pending capture in review queue
    SPA->>Caddy: /bridge/review-queue
    Caddy->>Bridge: get queue
    Bridge-->>Caddy: proposals + conflict choices
    Caddy-->>SPA: queue
    U->>SPA: approve a proposal
    SPA->>Caddy: /bridge/approve { proposalId }
    Caddy->>Bridge: approve
    Bridge->>Capture: redact -> atomicity -> dedup -> set --dryrun
    Capture-->>Bridge: digest / veto
    Bridge->>Capture: set (only if human approved)
    Capture-->>Bridge: write result
    Bridge-->>Caddy: outcome
    Caddy-->>SPA: result
```

The **view** path is read-only and goes through Caddy with the injected read token. The **graph** path is
the same — bounded traversal. The **approve** path is the only write path; it re-runs the capture
pipeline's gate sequence (redaction → atomicity → dedup → `set --dryrun`) via the bridge, never a direct
write. The write token is used only inside the bridge during an approve.
