# System Context (C1)

```mermaid
flowchart TB
    subgraph Practitioner
        U[Practitioner / human]
    end

    subgraph VM["One machine, loopback"]
        subgraph Viewer["viewer pod"]
            Caddy["Caddy\n(loopback entry)\n- serves SPA\n- proxies /api\n- injects Read token"]
            SPA["Console SPA\n(static, no-build)"]
            Bridge["Bridge\n(drives capture pipeline\n· read endpoints\n· review queue\n· conflict menus\n· approve -> set --dryrun)"]
            Caddy --> SPA
            Caddy --> Bridge
        end

        Host["Host (ASP.NET Core API)\nread / write capability"]
        DB[("Postgres + Apache AGE")]
        Blob[("MinIO blob store")]
        Seq["Seq log"]

        Caddy -->|"/api/*  + Read token| Host
        Caddy -->|"/bridge/*| Bridge
        Host --> DB
        Host --> Blob
        Host -. logs .-> Seq
        Bridge -.->|"invokes capture scripts"| Bridge
    end

    U --> Caddy
```

The human opens a single loopback URL (Caddy). Caddy serves the SPA, reverse-proxies `/api` to the Host
with the read token injected, and routes `/bridge/*` to the bridge. The bridge drives the capture
pipeline; the write token is held server-side, never in the browser.

The Host is unchanged: it still owns the API, the capability split, Postgres/AGE, MinIO and Seq. The
viewer pod is additive.

## Boundaries

- **Loopback only.** Caddy and the bridge bind localhost.
- **Capability split.** Read ambient (Caddy injects read token); write never ambient (bridge holds the
  write token, used only on approve).
- **No model.** Neither the SPA nor the bridge makes a judgement the capture skill does not already make.
