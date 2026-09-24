# mimisbrunnr-dossier

A **read-only dossier composer** for the contextual-knowledge-export feature (HLD-005 / BRD-002). The
Host API assembles a deterministic **bundle**; this skill applies **judgement** to compose the ordered,
consolidated, cited **dossier** and its **findings**, applies an optional **focus**, writes a local
gitignored artefact, and **writes nothing back to the store**.

Read `SKILL.md` for the invocation contract and `AGENTS.md` for the composition contract. The design and
its quality bar live in `docs/hlds/005-contextual-export/`.

The skill is **read-only by construction** (LADR-08 / NFR-06): the composition module exposes no write
operation, so the zero-write guarantee is structural rather than a behaviour the agent must remember to
follow. It never calls a model from Application or Host — judgement lives here and nowhere else.

```bash
python3 -B .agents/skills/mimisbrunnr-dossier/scripts/dossier_composer.py \
  bundle --body '{"repo":"kingstown","widenDepth":3}'          # the deterministic bundle (bodies hydrated), not the priced /preview
python3 -B .agents/skills/mimisbrunnr-dossier/scripts/dossier_composer.py \
  compose --bundle bundle.json --focus architecture \
  --out .context/mimisbrunnr-dossier/architecture.md           # compose + write the artefact
```

## Test

```bash
python3 -B .agents/skills/mimisbrunnr-dossier/tests/run_tests.py
```

## Related

- `docs/hlds/005-contextual-export/` — the HLD: LADRs, NFRs, the determinism boundary.
- `.agents/skills/mimisbrunnr-context-memory/` — the capture skill (sole **writer**); this skill is a
  reader.
- `.agents/skills/mimisbrunnr-understanding/` — the load/transfer sibling (HLD-007).
