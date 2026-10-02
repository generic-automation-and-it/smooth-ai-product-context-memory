# Heimdallr Find Session Metadata

Reports the current session's binding metadata to the **console**: tickets, repository,
initiative. Heimdallr is the watcher of the gods — he sees and hears all, which is what
this scanner does for a session before an export binds it.

Offline, read-only, no store call. The operator binds; this skill only reports.

```bash
python3 -B .agents/skills/mimisbrunnr-heimdallr-find-session-metadata/scripts/find_session_metadata.py [--json] [--initiative NAME]
```

| Field | Source |
|---|---|
| repository | `git remote get-url origin` → `owner/repo` |
| tickets | branch name + last 10 commit subjects |
| initiative | `--initiative` flag only, else `unknown` |

Feed the answers straight into the export flags:
`export <input> --tickets ... --repository ... --initiative ...`.
