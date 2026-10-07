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
| tickets | branch name + last 10 commit subjects; credential-shaped candidates are withheld (counted, never shown), and so is a credential-shaped branch name; a failed `git log` is disclosed (`commitsUnavailable`), never an empty history |
| initiative | `--initiative` flag only, else `unknown` |
| root | the checkout scanned (`--repo-root DIR`, else the working directory) |

The report is evidence, not a binding. Before passing any of it to the export flags, check
`rootMatches` (whether the scanned checkout is the one you asked for with `--repo-root`; `root` is for
display and is withheld outside your home folder), and choose which reported tickets the export is actually about —
the commit tickets come from the last ten subjects and can belong to unrelated work. Then pass only
those: `export <input> --tickets <the relevant ones> --repository ... --initiative ...`.
