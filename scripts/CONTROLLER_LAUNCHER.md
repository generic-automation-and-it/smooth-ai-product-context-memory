# Controller launchers

One-command launchers for the **release controller** — the container image that packages the Aspire
AppHost, DCP and dashboard and starts the API plus PostgreSQL, blob storage and Seq.

| Script | Platform | Interpreter |
|---|---|---|
| [`run.sh`](run.sh) | macOS, Linux | bash 3.2 or later (the macOS default qualifies) |
| [`run.ps1`](run.ps1) | Windows, or anywhere `pwsh` 7 runs | PowerShell 7.0 or later |

These exist because the manual `docker run` in [`docker.md`](../docs/wiki/docker.md) is eleven flags long and
several of them are load-bearing in ways a mistyped flag does not report. See
[What this automates](#what-this-automates) for which, and [Security](#security) before running.

**Standalone.** Copy either file out of the repository and run it. They read nothing from the checkout
and need no .NET SDK — only `bash`, `docker` and `openssl` (or PowerShell 7 and the docker CLI). The
optional version-drift report is the only thing that looks for a git checkout, and it degrades to a
plain image digest when there is none, so a copy in `~/bin` or on a jump host behaves identically.

```bash
scripts/run.sh                              # pull, start, print the dashboard login URL
scripts/run.sh status                       # containers, ports, data root
scripts/run.sh logs                         # controller log
scripts/run.sh stop                         # graceful; keeps the corpus
```

```powershell
./scripts/run.ps1
./scripts/run.ps1 -Verb status
./scripts/run.ps1 -Verb stop
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
(`run.sh`) or `-DataRoot` (`run.ps1`), or pass `MIMIS_DATA_ROOT=off` / omit `-DataRoot` to fall back
to engine-managed named volumes.

**Adopting a data root is a fresh start, not a move.** A volume that is not bound to the folder the
controller expects is refused before anything changes. To carry a corpus across: `snapshot` and
`verify` it, `reset` the installation, start on the data root, then `restore --force`.

## Credentials

Five values, all random on first run: the PostgreSQL password, the blob store key and secret, and
distinct read and write API tokens. Resolution order for the two tokens is **your `CONTEXT_MEMORY_*`
tokens → stored file → adopt-provisioned → generate**; for the three engine secrets it is **environment
→ stored file → generate**. The client tokens you already hold are the **master**: when
`CONTEXT_MEMORY_READ_TOKEN` / `CONTEXT_MEMORY_WRITE_TOKEN` exist (environment; for `run.sh` also the
shell profile; for the write token also `~/.mimisbrunnr/credentials`), the controller is started with
them and its stored pair follows them. Otherwise a stored value is never rewritten or dropped, because
release mode binds the tokens into the running Host and rotating one 403s every client that holds it.

**This file alone is not enough for a checkout's skills.** It carries the container's `Parameters__*`
names; the skills read `CONTEXT_MEMORY_*`, which `scripts/provision-credentials.sh` writes to
`.context/mimisbrunnr.env`. When that file exists the launcher **adopts** its token values rather than
minting its own, so the two halves are the same values and cannot drift into a 403. Both launchers then
publish the client side: `CONTEXT_MEMORY_BASE_URL`, `CONTEXT_MEMORY_READ_TOKEN` and the decision-gate
settings as **environment variables** — `run.sh` adds each one that is missing to a marked block in your
shell profile, `run.ps1` sets it in the Windows User scope — and the write token alone to
`~/.mimisbrunnr/credentials`. So **no provisioner is needed** for the skills on any platform.

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
/usr/bin/env 'Parameters__api-read-token=…' 'Parameters__api-write-token=…' scripts/run.sh up
```

```powershell
$env:'Parameters__api-read-token' = '…'   # or [Environment]::GetEnvironmentVariable('Parameters__api-read-token')
./scripts/run.ps1
```

A value supplied this way is used for that run and deliberately **not** written to the file; the
launcher says so on every run, because forgetting to re-export it means the next run mints a
different token.

This is the same property that silently broke every image publish between 2026-09-17 and 2026-10-01:
the engine hands the controller `Parameters__api-read-token`, and a shell drops env names that are
not identifiers. See [`scripts/AGENTS.md`](AGENTS.md).

## Options

| `run.sh` | `run.ps1` | Default | Meaning |
|---|---|---|---|
| `MIMIS_ID` | `-Id` | `default` | Installation id; names every resource |
| `MIMIS_HOME` / `MIMIS_DATA_ROOT` | `-DataRoot` | `~/.mimisbrunnr` | Host folder for data and secrets. Either launcher puts `controller.env` in this home, never inside the volumes folder, so moving an installation between them finds the secrets in the same place. Pass `-DataRoot` the volumes folder (`…\.mimisbrunnr\volumes`) or the home itself; both resolve to the same home. |
| `MIMIS_DATA_ROOT=off` | *(omit `-DataRoot`)* | — | Engine-managed named volumes |
| `CONTROLLER_IMAGE` | `-Image` | `…-apphost:latest` | Digest-pinned references are honoured as given |
| `MIMIS_SOCKET` | `-EngineSocket` | per platform | Engine socket or named pipe |
| `MIMIS_BIND_ADDRESS` | `-BindAddress` | `127.0.0.1` | Address the controller binds workload ports on |
| `MIMIS_SELINUX` | *(auto)* | auto | Force the `:z` bind label on or off |
| `P_HOST`, `P_POSTGRES`, `P_BLOB`, `P_BLOB_CONSOLE`, `P_SEQ`, `P_DASHBOARD`, `P_OTLP` | `-HostPort`, `-PostgresPort`, … | see below | Host ports |
| `MIMIS_PROFILE` | *(User-scope env vars)* | by login shell | Shell profile the client settings are added to; `off` leaves every profile alone |
| `CONTEXT_MEMORY_BASE_URL` | `CONTEXT_MEMORY_BASE_URL` | — | When set, the master for the API port; a disagreeing `P_HOST` / `-HostPort` is refused |

Defaults are the documented release ports — `5141 / 5432 / 9000 / 9001 / 5341 / 15278 / 19075`. They
are also what `provision-credentials.sh` writes into `CONTEXT_MEMORY_BASE_URL` and what the
context-memory client defaults to, so moving them here would break every skill and client the moment
a controller started. `MIMIS_SHIFT_PORTS=1` restores a set of shifted ports (`25141 / 25432 / …`) for the
rare case of running a controller and the development AppHost side by side. The launcher refuses to start
on a port already in use rather than letting the engine report it later.

**`CONTEXT_MEMORY_BASE_URL` is the master for the API port.** When it exists — in the environment, or in
the shell profile for `run.sh` — the API is published on its port, and a `P_HOST` / `-HostPort` that
disagrees is refused rather than resolved, because either answer silently strands one side. It must be a
loopback `http://localhost:<port>` with an explicit port. When it does not exist, the run's own port
(`5141`, or `25141` under `MIMIS_SHIFT_PORTS=1`) is what gets published.

**Client settings are environment variables; the write token is the one exception.** An exported variable
reaches every process the shell starts, and the read client refuses to start beside a write token — the
guarantee that the read surface can never mutate. So the write token lives only in
`~/.mimisbrunnr/credentials` (mode 600, rewritten to hold that one key), the capture client reads it there
on a write route when it is not in its environment, and the launcher removes a write-token export it
finds in its own managed profile block. A `~/.mimisbrunnr/credentials-read-only` left by an earlier
release is no longer read by anything; the launcher names it and leaves deleting it to you.

**The profile block is add-only.** A setting already exported, or assigned anywhere in the profile, is
never added again and never changed, so a hand-tuned value survives every run; a setting that is missing
is added at its default (or, for the base URL and read token, at the value this stack serves). A run
with nothing missing leaves the profile byte-identical. Every write is preceded by a mode-600 backup.
`MIMIS_PROFILE` names the profile; by default it follows your login shell (`~/.zshrc`, `~/.bashrc`, else
`~/.profile`), and `MIMIS_PROFILE=off` leaves every profile alone.

**A credential file that cannot be used is refused, not overwritten — and the refusal covers all three.**
`run.sh` asks the same four questions of `$env_file` and of the machine credential file before it
reads or writes either: not a symlink, a regular file, readable, and free of NUL bytes. The interesting ones
are the last two, because nothing used to catch them. An unreadable `controller.env` was regenerated in
full — five fresh secrets at exit 0, a new token pair on the Host and a 403 for every client still
holding the old one — and a NUL byte made the text filter that copies a credential file treat it as
binary and emit nothing, silently replacing an operator's keys with the defaults. Both now stop the run
and name the file. A symlink is refused for the same reason as before: `[ -f ]` follows it, so the
`mv` replaced a shared credentials file with a regular one and froze every client reading the shared
path on tokens the run had rotated away.

## Platform differences

Both `run.sh` paths — macOS and Linux — need only `bash` (3.2 or later), `docker`, `curl` and
`openssl`, and behave identically. The differences below are the ones that actually bite.

|  | macOS | Linux (native) | Windows |
|---|---|---|---|
| Engine socket | `/var/run/docker.sock` | same | `\\.\pipe\docker_engine` |
| `BindAddress` | `127.0.0.1` | `127.0.0.1` for host access; **bridge gateway** if containers must reach the controller | `127.0.0.1` |
| SELinux | n/a | **`:z` required** on Fedora/RHEL — `--mount` cannot carry a label, so the launcher emits `-v … :z` | n/a |
| `host.docker.internal` | not needed | `--add-host host.docker.internal:host-gateway` | not needed |
| Data root | `/Users` shared by default | native | **off by default** |
| Credential file mode | `chmod 600` enforced | `chmod 600` enforced | **no-op** — inherits the profile folder's ACL |

### macOS

Works as-is on Docker Desktop. The default data root lives under `/Users`, which Desktop shares by
default; if you relocate `MIMIS_HOME` outside a shared path, Desktop mounts it as an empty folder, which
is indistinguishable from a fresh install and silently orphans the previous corpus. The launcher warns
when it sees a root outside `/Users` or `/home`.

### Linux

Nothing extra on Docker Desktop. Two things are genuinely required on a **native** engine:

- **SELinux.** On Fedora/RHEL an enforcing host refuses a bind without a label. Detected from
  `/sys/fs/selinux/enforce`; override with `MIMIS_SELINUX=1` / `=0`.
- **`BindAddress`.** `127.0.0.1` is enough for host access. If other *containers* must reach the
  controller, set it to the bridge gateway (`docker network inspect bridge`) and add
  `--add-host host.docker.internal:host-gateway` — Docker Desktop's VM gateway is not the same address
  and must not be copied across.

### Windows

`pwsh ./scripts/run.ps1`. The data root defaults **off** because Docker Desktop there reports a bind's
host path as `/run/desktop/mnt/host/c/Users/…`, and the controller's entrypoint only normalises the
`/host_mnt` prefix that macOS and Linux Desktop report — a data root would fail its backing check.
`-DataRoot` opts in and warns; that path is unverified. `chmod` is a no-op, so restrict the folder's ACL
on a shared machine.

### Rootless Docker and Podman

Not wired up. Point `MIMIS_SOCKET` at the rootless socket and set `EngineConfiguration__Kind=podman` in
the environment; that remains a manual path.

## When `up` fails

`up` waits for `/health` and exits non-zero if the API never answers, because an earlier draft returned
immediately and printed the API URL while the container was crash-looping — "the script ran" and "the
app started" looked identical. When it fails, the containers are up and the fault is inside one of them.
In order of likelihood:

1. **Postgres rejected the password.** The data root was initialised under a different one, and
   PostgreSQL fixes its password on first init and ignores it forever after, so a regenerated one can
   never match. `docker logs mimisbrunnr-<id>-postgres 2>&1 | grep -i auth | tail -3`
2. **The token pair drifted** between the controller and `CONTEXT_MEMORY_*`. `run.sh logs`, look for 403.
3. **Migrations pending.** `run.sh logs`, look for `Migration`.

Recovery for (1) is either the credential file the data root was built with, or a clean start: `reset`
the installation and re-provision. Adopting a data root is a fresh start, not a move.

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

## Using the credentials from your shell

`up` already adds every missing client setting to your shell profile, so there is usually nothing to do
but open a new shell. To print the settings instead — for a different shell, or to inspect them:

```bash
scripts/run.sh env-export                    # macOS / Linux:  export KEY='value'
scripts/run.sh env-export powershell         # Windows:          $env:KEY = 'value'
scripts/run.sh env-export --profile ~/.bashrc  # add the missing ones to another profile
```

Each value printed is the one you configured — environment, else profile — and the default only where
you have none.

- **The write token is never printed.** Evaluating it would make it ambient, and the read client refuses
  to start beside one. It lives in `~/.mimisbrunnr/credentials`, which the capture client reads itself.
- **Neither is the decision API key.** It is your secret to export; the launcher never writes it.
- **Only `CONTEXT_MEMORY_*` names are printed.** The controller's own `Parameters__*` names contain
  hyphens and cannot be assigned by any shell — which is why they only ever travel by `--env-file`.
- **A session started before the profile changed has not inherited it.** Open a new terminal, or restart
  the app that launched the agent.

`env-export` is deliberately its own verb: `up` never prints a token to a terminal that may be
scrolled back, recorded, or read over someone's shoulder. You choose to run it, and choose where it
goes.

## Harness

`scripts/test-run-launcher.sh` covers the credential, profile and validation paths against a scratch
`MIMIS_HOME` — no container engine, no network, and it never reads or writes the operator's real
credentials or shell profile. It runs in the PR gate ("Test controller launcher").

Every case runs under `env -i` with a scratch `HOME`, `MIMIS_HOME`, machine-credential path and
`MIMIS_PROFILE`: the launcher now takes the base URL and tokens from the environment and writes the
profile, so an inherited `CONTEXT_MEMORY_*` name would test the operator's configuration and an inherited
`HOME` would edit their `~/.zshrc`. An earlier version redirected only the home and overwrote live
credentials. `run.ps1` has no equivalent harness; its non-Windows path runs under `pwsh` on macOS, and the
Windows User-scope branch is unverified on any runner.

## Writing the credentials into a shell profile

Use the flag, not a redirect:

```bash
scripts/run.sh env-export --profile ~/.zshrc    # or ~/.zprofile, or any file; defaults to ~/.zshrc
scripts/run.sh env-export --profile $PROFILE powershell
```

`>` and `>>` differ by one character and do opposite things to a profile: one empties it, the other grows
it without bound. That is the wrong thing to leave to a habit, so the flag writes a bounded, marked block:

```
# >>> mimisbrunnr — managed by scripts/run.sh env-export --profile >>>
export CONTEXT_MEMORY_BASE_URL='…'
export CONTEXT_MEMORY_READ_TOKEN='…'
# <<< mimisbrunnr <<<
```

Only the region between the markers is ever written. Everything else in the file is preserved byte for
byte, including on a first write, so a populated profile is never emptied. Re-running rewrites the three
lines in place: no duplicates, and a rotated token replaces the old one instead of outliving it in a
world-readable file. A profile is always backed up before any modification to
`<file>-mimisbrunnr-<timestamp>(-<random>)` — both the first append and a later in-place rotation — so
the operator never loses the version they were editing, and a symlinked target is refused.

## Limitations

- **Coverage is uneven.** `scripts/test-run-launcher.sh` now covers the bash launcher's credential,
  validation and export paths in the PR gate. What remains untested is the part that needs a container
  engine — the start/restart/stop lifecycle, the orphan cleanup, the readiness gate against a
  deliberately broken database — and `run.ps1` in full, since its Windows paths cannot execute on a
  Linux or macOS runner at all.
- Neither script verified that the container reached a healthy state in its first release: it printed the
  login URL and returned while the API container was crash-looping. Both now wait for `/health` and exit
  non-zero with the cause ranked. (Corrected — an earlier version of this file said neither script had
  the gate, which was true of `run.ps1` at the time and false of the pair.)
- The Windows data root is unverified, as above.

## See also

- [`docker.md`](../docs/wiki/docker.md) — the controller, its contract, upgrades and recovery
- [`setup.md`](../docs/wiki/setup.md) — API credentials and first run
- [`apphost-container-entrypoint.sh`](apphost-container-entrypoint.sh) — the
  ownership and lifecycle rules these launchers defer to