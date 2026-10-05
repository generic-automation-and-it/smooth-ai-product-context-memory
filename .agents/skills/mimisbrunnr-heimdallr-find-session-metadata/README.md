# Heimdallr Find Session Metadata

Reports the current session's binding metadata to the **console**: tickets, repository,
initiative. Heimdallr is the watcher of the gods — he sees and hears all, which is what
this scanner does for a session before an export binds it.

Offline, read-only, no store call. The operator binds; this skill only reports.

```bash
python3 -B .agents/skills/mimisbrunnr-heimdallr-find-session-metadata/scripts/find_session_metadata.py [--json] [--initiative NAME] [--repo-root DIR]
```

| Field | Source |
|---|---|
| repository | `git remote get-url origin` → `owner/repo` |
| tickets | branch name + last 10 commit subjects; credential-shaped candidates are withheld (counted, never shown) |
| initiative | `--initiative` flag only, else `unknown` |
| root | the checkout scanned (`--repo-root DIR`, else the working directory) |

Feed the answers straight into the export flags:
`export <input> --tickets ... --repository ... --initiative ...`.
