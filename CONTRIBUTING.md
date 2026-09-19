# Contributing

Thanks for taking an interest in Mímisbrunnr. This page is deliberately short — it links to
the rules rather than restating them, so there is one source of truth for each.

## Getting set up

You need the **.NET 10 SDK** and a container runtime (Aspire starts PostgreSQL + AGE, MinIO
and Seq for you).

```bash
dotnet build SmoothAiProductContextMemory.slnx
dotnet test  SmoothAiProductContextMemory.slnx
dotnet run --project src/SmoothAiProductContextMemory.AppHost   # full dev stack
```

`AGENTS.md` has the complete command catalogue, including the benchmark harnesses and the
stack teardown scripts. Note that **exiting AppHost does not stop the stack** — use
`scripts/stop-dev-stack.sh` (keeps data) or `scripts/reset-dev-stack.sh` (destroys volumes).

## Making a change

1. **Branch off `main`** as `<type>/<issue>-short-description`, e.g. `feat/1234-add-user-export`.
2. **Commits and the PR title** follow [Conventional Commits](https://www.conventionalcommits.org).
   The PR title additionally carries the ticket: `<type>[{ticket}]: <description>`. It becomes the
   squash commit on `main`, so make it stand alone.
3. **Update the nearest `*AGENTS.md`.** Every PR should create or update at least one. Record the
   change in the *nearest localized* changelog — the root one is for solution-global changes.
4. **Open the PR** using the template. It is filled in, not deleted.

Branch types, commit format and PR title rules are specified in
[`.agents/rules/git/`](.agents/rules/git/) — that is the authority, and it is enforced.

## Two things that trip people up

**Fill in `## Skip Areas / Known Issues`, or delete the section.** The AI review gate parses that
heading to learn which findings you skipped on purpose. A note tucked anywhere else in the PR body
is invisible to it, and the same finding gets raised again every round. If you have nothing to
skip, remove the section rather than leaving an empty bullet.

**Memory content must never be logged** — not at any level, not in error paths, not in
diagnostics. Log identifiers, counts and outcomes; never the content they describe. Content
addresses stay out of information-level logs too, since an address plus store access is equivalent
to the content. This is a context-memory service: the stored text is the product. The full
requirement is [HLD-001 NFR-05 *Confidentiality*](docs/hlds/001-context-memory-storage/nfrs/NFR-05-confidentiality.md).

## Review

Pull requests get an automated AI review in addition to a human one. It comments inline; you are
expected to either fix a finding or record it as a skip with a reason. Details, and the full CI
contract, are in [`docs/wiki/ci.md`](docs/wiki/ci.md).

## Reporting problems

- **Bugs and features** — open an issue.
- **Security vulnerabilities** — do **not** open an issue. See [SECURITY.md](SECURITY.md).
- **Conduct** — see [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

Contributions are accepted under the [MIT License](LICENSE).
