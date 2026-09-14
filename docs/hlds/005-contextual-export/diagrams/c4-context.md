# Diagrams — Contextual knowledge export

The **C1 System Context** below is the mandatory floor. One further diagram earns its place and lives in
its own file: [flow — selection, bundle, composition, findings](./flow-selection-and-composition.md),
because the boundary between deterministic selection and model judgement is the design's thesis and is
invisible in a context diagram.

## System Context (C1)

A practitioner asks the dossier skill for everything the store knows about a slice of work. The skill
requests one **bundle** from the existing context-memory API, which resolves the anchors relationally,
widens over the graph within a caller-supplied bound, hydrates bodies from blob storage, and returns a
manifest of what it selected, reached and cut. The skill composes the **dossier** — ordered, collapsed,
cited, with its findings — and writes it as a local artefact. Nothing is written back to the store.

The recipient is outside the system boundary on purpose: they receive a file the practitioner chose to
give them, and hold no access to the store.

```mermaid
C4Context
    title Contextual knowledge export — System Context

    Person(practitioner, "Practitioner", "Requests an export, reads it, acts on its findings.")
    Person_Ext(recipient, "Recipient — human or agent", "Reads a dossier handed to them. No access to the store.")

    System(dossierSkill, "Dossier skill", "Read-only. Requests a bundle, composes the ordered document and its findings. Owns all judgement.")
    System(api, "Context-memory API", "Resolves anchors, widens over the graph within the caller's bound, hydrates bodies, reports the manifest. No judgement.")

    SystemDb(store, "PostgreSQL + Apache AGE", "Memories, versions, groups, tags and the relationship graph. Source of truth.")
    SystemDb(blobs, "Blob storage", "Content-addressed memory bodies.")
    System_Ext(artefact, "Dossier artefact", "Local Markdown file. Sensitive by default; not committed or synchronised.")

    Rel(practitioner, dossierSkill, "Requests an export for a slice")
    Rel(dossierSkill, api, "Requests a bundle", "HTTP")
    Rel(api, store, "Anchor resolution + bounded widening, one session")
    Rel(api, blobs, "Hydrates bodies")
    Rel(api, dossierSkill, "Bundle + manifest", "HTTP")
    Rel(dossierSkill, artefact, "Writes the composed dossier")
    Rel(practitioner, artefact, "Reads")
    Rel(practitioner, recipient, "Hands on, at their discretion")

    UpdateRelStyle(dossierSkill, artefact, $offsetY="-10")
    UpdateRelStyle(practitioner, recipient, $offsetY="-20")
```

**What is deliberately absent from this diagram:**

- **Any arrow back into the store.** An export is a read (NFR-06); the composing skill has no write capability at all (LADR-08).
- **The forensic dump.** A separate whole-store projection with opposite rules; see LADR-01.
- **The capture skill.** It remains the sole *writer* and plays no part in an export (LADR-08).
- **Any network dependency beyond the local API.** The store is local and offline-capable; ticket-tracker relationships are out of reach for that reason (LADR-09).
