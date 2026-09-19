# Security Policy

## Supported versions

| Version | Supported |
|---|---|
| `main` / `latest` image | ✅ |
| Anything older | ❌ |

This project has no SemVer release line. Fixes land on `main` and reach the `latest` and
`sha-<short-sha>` tags on [GHCR](https://github.com/generic-automation-and-it/smooth-ai-product-context-memory/pkgs/container/smooth-ai-product-context-memory).
Pin a digest if you need immutable deployment identity.

## Reporting a vulnerability

**Do not open a public issue, pull request or discussion for a security problem.**

Use GitHub's private vulnerability reporting:

**[Report a vulnerability →](https://github.com/generic-automation-and-it/smooth-ai-product-context-memory/security/advisories/new)**

If that form is unavailable to you, contact the maintainers of
[`generic-automation-and-it`](https://github.com/generic-automation-and-it) privately instead. Do
not disclose details publicly until a fix has shipped.

Please include what you need to make it reproducible: affected version or image digest, the
component, steps or a proof of concept, and the impact as you see it.

### What to expect

- **Acknowledgement** within a few days.
- An assessment and a rough remediation timeline once the report is triaged.
- Credit in the advisory if you want it, and coordinated disclosure once a fix is available.

This is a small project maintained on a best-effort basis. There is no bug bounty.

## Scope

In scope: the API host, the context-memory stores, the container images, and the deployment
guidance in [`docs/wiki/`](docs/wiki/).

Two things that look like vulnerabilities and are documented, accepted trade-offs rather than
reports we can act on:

- **The CI review gate runs an unauthenticated loopback listener** for the life of its job, so
  anything executing in that job can spend gateway quota. This is inherent to running the gate on
  a hosted runner — see [`docs/wiki/ci.md`](docs/wiki/ci.md).
- **The dev stack is not hardened.** Aspire brings up PostgreSQL, MinIO and Seq with development
  defaults on loopback. It is not a deployment target.

Do report anything that leaks stored memory content. This is a context-memory service — the text
it holds is the asset, and [HLD-001 NFR-05 *Confidentiality*](docs/hlds/001-context-memory-storage/nfrs/NFR-05-confidentiality.md) requires that it is never
logged at any level, including error and diagnostic paths. A path that puts content, or a content
address, into a log is a real finding.
