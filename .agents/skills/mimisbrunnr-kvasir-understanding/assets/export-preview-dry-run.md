DRY RUN — nothing was written, and nothing was created.
  would write: memory ({{total}}){{group}}
  would not create: anything under an existing group, because no group was resolved
The server-side `set --dryrun` veto runs at the start of a write, once a group exists. Re-run without `--dryrun` to capture.
