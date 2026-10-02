# Controller launchers

One-command launchers for the **release controller** — the container image that packages the Aspire
AppHost, DCP and dashboard and starts the API plus PostgreSQL, blob storage and Seq.

| Script | Platform | Interpreter |
|---|---|---|
| [`run-controller.sh`](run-controller.sh) | macOS, Linux | bash 3.2 or later (the macOS default qualifies) |
| [`run-controller.ps1`](run-controller.ps1) | Windows, or anywhere `pwsh` 7 runs | PowerShell 7.0 or later |

These exist because the manual `docker run` in [`docker.md`](../docs/wiki/docker.md) is eleven flags long and
several of them are load-bearing in ways a mistyped flag does not report. See
[What this automates](#what-this-automates) for which, and [Security](#security) before running.

**Standalone.** Copy either file out of the repository and run it. They read nothing from the checkout
and need no .NET SDK — only `bash`, `docker` and `openssl` (or PowerShell 7 and the docker CLI). The
optional version-drift report is the only thing that looks for a git checkout, and it degrades to a
plain image digest when there is none, so a copy in `~/bin` or on a jump host behaves identically.

```bash
scripts/run-controller.sh                              # pull, start, print the dashboard login URL
scripts/run-controller.sh status                       # containers, ports, data root
scripts/run-controller.sh logs                         # controller log
scripts/run-controller.sh stop                         # graceful; keeps the corpus
```

```powershell
./scripts/run-controller.ps1
./scripts/run-controller.ps1 -Verb status
./scripts/run-controller.ps1 -Verb stop
```

## Running it again is a restart

A second run replaces the running controller: it stops it gracefully — which is the controller's own
shutdown path, and is what removes `mimisbrunnr-<id>-host` and the other three workloads — deletes the
controller container, pulls the newest image, and starts a fresh one. **Data is preserved**: no volume
is ever removed, and the data-root folders are left alone. Tokens are likewise never rotated, so
clients holding a token keep working across the restart.

Aspire reattaches without help. The controller is not a long-lived process with an opinion about
upstreams; DCP inside the new controller recreates all four workloads and re-registers them, and the
dashboard's resources track the new containers. Verified by container identity across a restart:

| | before | after |
|---|---|---|
| `mimisbrunnr-default-host` id | `066662e2…` | `57eec57a…` (recreated) |
| `/health` | `200` | `200` |
| corpus | 65 MB | 65 MB |
| same write token | `400` (route reached) | `400` |

Workload start order after a restart is blob/postgres/seq together, then the API once its dependencies
are healthy — the controller waiting on dependencies, not on a previous process.

Two consequences worth knowing:

- **An upgrade is a migration.** The new image applies its own migrations at startup, before it reports
  ready. Snapshot a corpus you care about first (`snapshot`, then `verify`), and note that rolling an
  image back does not roll back schema.
- **Orphaned workloads are cleaned up too.** After a hard kill the four containers can survive with no
  controller owning them. The launcher removes exactly those carrying this installation's ownership
  label and nothing else; a foreign container occupying one of the names is still refused by the
  controller's own preflight rather than deleted here.

The data-root binding is checked **before** anything is stopped. A mismatch is an operator mistake no
restart can fix, and tearing the stack down first would leave the machine with nothing running.

## What it does, and what it will not do

It starts **one** container: `mimisbrunnr-<id>-controller`. That controller starts the other four
(`mimisbrunnr-<id>-{host,postgres,blob-well,seq}`) through the mounted socket, and owns their
lifecycle, health and ownership checks. Do not start them directly — the rules live in
`scripts/apphost-container-entrypoint.sh`, and bypassing them is how a second controller ends up
deleting another installation's running workloads.

Three failure modes it prevents, each of which the documentation warns about but nothing enforces:

- **A second controller.** Starting one removes every other installation's running workloads. Volumes
  survive, so it looks survivable. Open defect, issue #159. The launcher refuses instead.
- **A label that does not match the installation id.** `--label com.docker.compose.project` must equal
  `smooth-mímisbrunnr-release-<id>` exactly. The controller cannot label itself, so a mismatch leaves
  the dashboard's container ungrouped in Docker Desktop — a cosmetic failure with no error message.
- **A stale `latest`.** The controller bakes its API image in at build time, and the pipeline
  publishes only on main pushes. A launcher that pulls only when the image is absent reuses a stale
  copy indefinitely; these always pull a mutable tag and then report the gap between the image's baked
  commit and your `HEAD`, because the API container is what you are actually testing.

## Host layout

```
~/.mimisbrunnr/controller.env                        # mode 600, the five release secrets
~/.mimisbrunnr/volumes/<id>/postgres-data/
~/.mimisbrunnr/volumes/<id>/blob-well-data/
~/.mimisbrunnr/volumes/<id>/seq-data/
~/.mimisbrunnr/volumes/<id>/host-context/
~/.mimisbrunnr/volumes/<id>/controller-state/         # mounted directly, not an engine volume
```

The corpus is a folder you can see, back up and delete. Point it elsewhere with `MIMIS_HOME`
(`run-controller.sh`) or `-DataRoot` (`run-controller.ps1`), or pass `MIMIS_DATA_ROOT=off` / omit `-DataRoot` to fall back
to engine-managed named volumes.

**Adopting a data root is a fresh start, not a move.** A volume that is not bound to the folder the
controller expects is refused before anything changes. To carry a corpus across: `snapshot` and
`verify` it, `reset` the installation, start on the data root, then `restore --force`.

## Credentials

Five values, all random on first run: the PostgreSQL password, the blob store key and secret, and
distinct read and write API tokens. Resolution order is **environment → stored file → generate**,
and the file is only ever *added to* — a stored value is never rewritten or dropped, because release
mode binds the tokens into the running Host and rotating one 403s every client that holds it.

They are generated once and reused. Deleting the file re-provisions from scratch; there is no
`--rotate`, deliberately, because a rotation is an operational decision rather than a side effect of
running a launcher.

Secrets reach the container through `--env-file` and never appear in `argv`, in output, or in a log.

### Supplying your own

The token parameters have hyphens in their names, which no shell accepts as an identifier:

```bash
export Parameters__api-read-token=…   # syntax error, in bash and in zsh alike
```

Use `env`, which takes the name as a string:

```bash
/usr/bin/env 'Parameters__api-read-token=…' 'Parameters__api-write-token=…' scripts/run-controller.sh up
```

```powershell
$env:'Parameters__api-read-token' = '…'   # or [Environment]::GetEnvironmentVariable('Parameters__api-read-token')
./scripts/run-controller.ps1
```

A value supplied this way is used for that run and deliberately **not** written to the file; the
launcher says so on every run, because forgetting to re-export it means the next run mints a
different token.

This is the same property that silently broke every image publish between 2026-09-17 and 2026-10-01:
the engine hands the controller `Parameters__api-read-token`, and a shell drops env names that are
not identifiers. See [`scripts/AGENTS.md`](AGENTS.md).

## Options

| `run-controller.sh` | `run-controller.ps1` | Default | Meaning |
|---|---|---|---|
| `MIMIS_ID` | `-Id` | `default` | Installation id; names every resource |
| `MIMIS_HOME` / `MIMIS_DATA_ROOT` | `-DataRoot` | `~/.mimisbrunnr` | Host folder for data and secrets |
| `MIMIS_DATA_ROOT=off` | *(omit `-DataRoot`)* | — | Engine-managed named volumes |
| `CONTROLLER_IMAGE` | `-Image` | `…-apphost:latest` | Digest-pinned references are honoured as given |
| `MIMIS_SOCKET` | `-EngineSocket` | per platform | Engine socket or named pipe |
| `MIMIS_BIND_ADDRESS` | `-BindAddress` | `127.0.0.1` | Address the controller binds workload ports on |
| `MIMIS_SELINUX` | *(auto)* | auto | Force the `:z` bind label on or off |
| `P_HOST`, `P_POSTGRES`, `P_BLOB`, `P_BLOB_CONSOLE`, `P_SEQ`, `P_DASHBOARD`, `P_OTLP` | `-HostPort`, `-PostgresPort`, … | see below | Host ports |

Defaults are `25141 / 25432 / 29000 / 29001 / 25341 / 15279 / 19076`, deliberately clear of the
documented `5141 / 5432 / 9000 / 9001 / 5341 / 15278` so a development AppHost and a release
installation can run side by side on one machine. The launcher refuses to start on a port already in
use rather than letting the engine report it later.

## Platform differences

|  | macOS | Linux (native) | Windows |
|---|---|---|---|
| Engine socket | `/var/run/docker.sock` | same | `\\.\pipe\docker_engine` |
| `BindAddress` | `127.0.0.1` | `127.0.0.1` for host access; **bridge gateway** if containers must reach the controller | `127.0.0.1` |
| SELinux | n/a | **`:z` required** on Fedora/RHEL — `--mount` cannot carry a label, so the launcher emits `-v … :z` | n/a |
| `host.docker.internal` | not needed | `--add-host host.docker.internal:host-gateway` | not needed |
| Data root | `/Users` shared by default | native | **off by default** |

Nothing extra is needed for Docker Desktop on macOS or Linux. Two things are genuinely required on
native Linux: the SELinux label (auto-detected from `/sys/fs/selinux/enforce`) and a bridge-gateway
`BindAddress` when containers must reach the controller.

**The data root defaults off on Windows** because Docker Desktop there reports a bind's host path as
`/run/desktop/mnt/host/c/Users/…`, and the controller's entrypoint only normalises the `/host_mnt`
prefix that macOS and Linux Desktop report — a data root would fail its backing check. `-DataRoot`
opts in and warns; that path is unverified on Windows.

Rootless Docker and Podman are a third case: point `MIMIS_SOCKET` at the rootless socket and set
`EngineConfiguration__Kind=podman` in the environment. Not wired into the launcher, so those remain a
manual path.

## Security

- **The engine socket grants administrative control of the host** with the engine user's privileges,
  potentially root. A read-only socket mount does not make API calls read-only. This is inherent to the
  controller design (LADR-005), not to these scripts, and it is why the controller image should not be
  exposed with an unauthenticated engine API.
- **`~/.mimisbrunnr/controller.env` is mode 600**, re-asserted on every run rather than only on
  creation, and written through a temporary file so an interrupted run cannot leave it half-written or
  world-readable. A symlink or directory at that path is refused rather than written through.
- **Secrets never reach `argv`**, so they stay out of the process table and shell history.
- **Ports bind to loopback.** Workload ports are published on `127.0.0.1` unless you override the bind
  address; the dashboard and OTLP endpoints are `-p 127.0.0.1:…`.
- **Context API bearer tokens are not multi-user authorization.** They separate read from write
  capability only. Keep the API and Seq on private interfaces.

## Limitations

- These scripts have **no automated harness yet**, so a change to them is reviewed rather than tested.
  Being under `scripts/` at least means a change to them triggers the PR gate (see the `scripts/**`
  path filter), unlike a home under `docs/`. A harness against a scratch `MIMIS_HOME` is the next
  step, and would have caught the credential-clobbering defect found while writing them.
- Neither script verifies that the container reached a healthy state; it prints the login URL and
  leaves readiness to you. `scripts/run-controller.sh status` and
  `curl localhost:<P_HOST>/health` answer that.
- The Windows data root is unverified, as above.

## See also

- [`docker.md`](../docs/wiki/docker.md) — the controller, its contract, upgrades and recovery
- [`setup.md`](../docs/wiki/setup.md) — API credentials and first run
- [`apphost-container-entrypoint.sh`](apphost-container-entrypoint.sh) — the
  ownership and lifecycle rules these launchers defer to