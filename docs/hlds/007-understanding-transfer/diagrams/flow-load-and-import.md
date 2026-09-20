# Flow — load vs import

```mermaid
sequenceDiagram
    participant Practitioner
    participant LoadSkill as mimisbrunnr-understanding
    participant Agent as Agent session
    participant Capture as mimisbrunnr-context-memory (capture path)
    participant Store as Mímisbrunnr store

    Practitioner->>LoadSkill: load material (Understanding export / foreign doc)
    LoadSkill->>LoadSkill: read input
    alt default (no --store)
        LoadSkill->>LoadSkill: render as cited grounding context
        LoadSkill->>Agent: inject context (no write)
        Note over LoadSkill,Store: store unchanged (NFR-01)
    else --store (import)
        LoadSkill->>Capture: hand material to capture path
        Capture->>Capture: preflight -> redact -> dedup/link -> atomicity
        Capture->>Store: write
        Capture-->>LoadSkill: digest + proposed/conflict flags
    end
```

The default is a read and writes nothing. `--store` is the one thing that turns the read into a
capture, and that capture goes through the existing write path (LADR-02, LADR-03).
