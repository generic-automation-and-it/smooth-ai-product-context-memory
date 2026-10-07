# Setup & Credentials

The canonical page for the API credential model — the one part of this product an operator cannot
infer from the code. It applies to **every** run mode (AppHost, direct Host run, bare container):
none of them has a default credential, so a reader who does not set the tokens gets a process that
exits at startup with no explanation.

## The two tokens

The API requires two distinct Bearer tokens, one per capability:

| Capability | Allows | Env var (server) |
|---|---|---|
| **Read** | query / history / blob / path / ticket-path / label-list / initiative-list / recall-feedback reads / dossier / snapshot preflight & status | `ApiAccess__ReadToken` |
| **Write** | everything the read token allows, plus create / update / link / group / label / initiative / snapshot triggers / recall-feedback reset | `ApiAccess__WriteToken` |

They must be **distinct** and **non-blank**. The authorizer validates in its constructor and throws if
either is blank or the two match, so an unconfigured run exits at startup.

They are runtime configuration only: the Host hashes them at load, never writes them to the store, and
never logs them. **Regenerating them invalidates no data** — deleting the file and re-running the
provisioner is a complete recovery procedure. Rotation costs nothing. See
[`docker.md`](docker.md) for the container form and the variable inventory.

## The variable-name mapping

This is the step that used to be the silent manual gap: the service and the skills read the **same two
values under different names**, and nothing bridged them.

| One value | Name the server reads | Name the skills read |
|---|---|---|
| read token | `ApiAccess__ReadToken` | `CONTEXT_MEMORY_READ_TOKEN` |
| write token | `ApiAccess__WriteToken` | `CONTEXT_MEMORY_WRITE_TOKEN` |
| base URL | — | `CONTEXT_MEMORY_BASE_URL` (default `http://localhost:5141`) |

`CONTEXT_MEMORY_BASE_URL` is used by the host-side skills only; the server derives nothing from it. The
server reads its tokens from the `ApiAccess` section, the skills read theirs from `CONTEXT_MEMORY_*`,
and both must carry the **same values**. The provisioner below writes both name forms into two gitignored
env files — one per parser grammar, described next — so they can never drift.

One skill-only knob is **not** a credential and has no server counterpart:
`CONTEXT_MEMORY_RECALL_DEADLINE` (seconds, `1`–`60`, default `60`) shortens a recall's one foreground
deadline. It bounds a single read and the whole `deepsearch` chain; an out-of-range or non-integer value
is **refused with `bad-deadline`, never clamped**, because a budget that silently becomes something else
is one the operator trusts and the system does not honour. The provisioner does not set it.

## The optional decision gate

`CONTEXT_MEMORY_DECISIONS_ENABLED=true` turns on a value gate: each memory or Understanding about to be
exported is scored by a **local decision model** for value to each target role, and a record no role
values is held back rather than written. **Off by default**, and nothing here is required — an operator
who never sets it runs exactly as before, with no model installed.

Everything is client-side. There is **no model in the Host or Application**, no server-side scoring, and
no API change. The gate is a quality signal only: a score never changes `status`, kind, or approval.

| Variable | Default | Meaning |
|---|---|---|
| `CONTEXT_MEMORY_DECISIONS_ENABLED` | `false` | Feature flag. Anything but exactly `true` leaves the gate skipped |
| `CONTEXT_MEMORY_DECISIONS_BASE_URL` | `http://localhost:11434` | Decision API origin (local Ollama) |
| `CONTEXT_MEMORY_DECISIONS_PATH` | `/v1/systemone` | Endpoint path (Jev-compatible) |
| `CONTEXT_MEMORY_DECISIONS_MODEL` | `nimble` | Decision model (`ollama pull nimble`) |
| `CONTEXT_MEMORY_DECISIONS_API_KEY` | *(empty)* | Bearer token, sent **only when non-empty**. Never printed by `env-export` or the profile |
| `CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY` | `0.85` | Pass threshold per role — a role must score **strictly above** it |
| `CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS` | `3` | Scoring rounds per record, counting the first |
| `CONTEXT_MEMORY_DECISIONS_ROLES` | `product-owner,designer,developer,tester,business` | Rubric roles to ask about |
| `CONTEXT_MEMORY_DECISIONS_BELOW_THRESHOLD` | `hold` | After the last attempt: `hold` (not exported) or `mark` (exported with `audience:*` tags) |
| `CONTEXT_MEMORY_DECISIONS_TIMEOUT` | `30` | Seconds per decision request |
| `CONTEXT_MEMORY_DECISIONS_DEDUP_ENABLED` | `false` | Turn on stage-3 of staged dedup: a model asks, per doubtful pair, whether two statements make the same claim. Off by default (stage 2 lexical candidates are still shown, as "possible duplicate (lexical only)") |
| `CONTEXT_MEMORY_DECISIONS_DEDUP_MIN_PROBABILITY` | `0.5` | Same-claim threshold — a pair scores **strictly above** it to be proposed as a duplicate |
| `CONTEXT_MEMORY_DECISIONS_DEDUP_MAX_PAIRS_PER_CANDIDATE` | `3` | Most pairs stage 3 asks the model about per candidate |
| `CONTEXT_MEMORY_DECISIONS_DEDUP_MAX_PAIRS_PER_BATCH` | `20` | Most pairs stage 3 asks across a batch; beyond it, `unexamined (budget)` is disclosed per candidate |
| `CONTEXT_MEMORY_DECISIONS_DEDUP_MAX_RESULTS` | `5` | Most lexical candidates stage 2 retrieves per subject |

**Where the settings live.** `scripts/run.sh` writes all fifteen with their defaults to
`~/.mimisbrunnr/credentials` — **add-only**, so turning the gate on survives every restart, which is the
case a write-every-time rewrite cannot survive. `env-export` publishes the fourteen non-secret ones to a shell
profile; the API key is deliberately excluded, because a token written to a terminal that gets scrolled
back, recorded, or read over a shoulder is disclosed. The read-only credential file stays minimal and
carries **no** decision settings: a worker that cannot mutate has no use for a decision endpoint, and
publishing the key there would hand a read-only credential a second secret.

**Check it before enabling it:**

```bash
python3 -B .agents/skills/mimisbrunnr-odin-context-memory/scripts/context_memory_client.py decisions-probe
```

Reports `disabled`, `unreachable`, `model-missing`, or `ok`, and the endpoint origin — never the key.

**Three properties worth knowing before you rely on it:**

- **A failed gate is never a low score.** `unreachable`, `timed-out`, `http-<code>`, `bad-response` and
  `oversize` all **keep** the record and disclose the reason. A decision model that is down must not block
  a capture, and a hung model must never read as "this record is worthless".
- **Redaction runs before any model call**, and a redactor that cannot run means **no request is made at
  all**. Sending unscrubbed record content to a model is the one outcome the gate exists to prevent.
- **The attempt counter is the script's, not yours.** A ledger keyed by record identity bounds the
  rewrite loop, so re-asking cannot buy more attempts.
- **A saturated score is not a confident one.** Each scored record carries a `discrimination` block
  reporting the `margin` between the highest-scoring role and the next one, plus every role tied at
  the top. On the calibration fixture the mean best-role is `0.99` and 3 of 21 records carry roles
  scoring exactly `1.00000`, so a three-role `passingRoles` set is usually ambiguous rather than
  thrice-confirmed. It never affects a verdict — it reports how much to trust the shape of the result,
  because `passingRoles` becomes `audience:*` tags downstream and a tie would otherwise read as a
  clear win.

**The threshold is a starting value, and it is now measured rather than merely asserted.** A labelled
fixture of 21 records — a junk-to-specific gradient across six domains — is committed with the skill and
scored against the shipped gate:

```bash
python3 -B .agents/skills/mimisbrunnr-odin-context-memory/tests/score_decisions_calibration.py
python3 -B .agents/skills/mimisbrunnr-odin-context-memory/tests/score_decisions_calibration.py --check-determinism
```

At the shipped default the gate scored **precision 1.00, recall 1.00** — mean best-role **0.14** on the six
hold-side records against **0.99** on the fifteen that state a checkable fact. Re-running prints the
committed figures beside a fresh run and names anything that moved; `--check-determinism` scores the whole
fixture three times and fails if the runs differ, because determinism is what makes a threshold pinnable at
all. Three things that measurement settles, each of which had been argued from impression:

- **The threshold is not a sensitive knob here.** The scores are bimodal — nothing between `0.21` and
  `0.96` — so any bar in that band gives identical verdicts, which is why the default moved to `0.85`
  without changing a single verdict on the fixture. What it does change is attribution: roles clearing
  the bar fall from 27 of 30 expected to 24 of 30. Both runs are recorded in the fixture.
- **Tightening the rubric does not tighten the gate.** Two rewrites — enumerating concrete nouns, and
  narrowing the criteria — both dropped precision to `0.94` and lost six expected roles, because the
  `instructions` carry the framing and a narrower *criteria* block did not narrow the *question*. Both
  rejected variants are recorded with their numbers in the fixture.
- **Do not apply the vendor's fitted temperature.** Nimble was fitted at `T=2.179` so probabilities match
  correctness rates, not so they separate better. For a binary answer the logit gap is exactly recoverable
  from the returned probability, so the correction is trivial to apply and **it makes the gate worse**:
  junk rises from `0.14` to `0.31` and separation falls from `0.85` to `0.62`.

Role attribution is the weaker half and the measurement says so: `tester` clears 15 of 21 records and
carries almost no negative information, and `product-owner`/`business` stay correlated at `r=0.92` even
after their shared vocabulary was removed. A single `choice` question was measured as an alternative and
was **not** more accurate (13 of 15 labelled records against 14). Neither is fixable by rewording — two
rewrites both dropped precision to `0.94`, because the `instructions` carry the framing and a narrower
`criteria` block did not narrow the *question*. Roles beyond the five, and exemptions for `self`-scope or
agent-facing records, remain open decisions — see the change row in the capture skill's `AGENTS.md`.

The gate has **not** been exercised against a remote Jev-compatible endpoint, which is the configuration
these settings exist to permit. Its transport guards for that case (https required off-loopback, bearer
key required, userinfo refused) are unit-tested, but no remote endpoint has answered a request.

## One-command provisioning

For a local deployment, run the provisioner once:

```bash
scripts/provision-credentials.sh
```

It writes two distinct random tokens (`openssl rand -hex 32`, each) to two gitignored env files (`*.env`
and `.context/` are both ignored, mode 600). `.context/mimisbrunnr.env` is **sourceable** and carries
**both** name forms that are valid shell identifiers — the server `ApiAccess__*` names and the skill
`CONTEXT_MEMORY_*` names — and the standalone Host reads its `ApiAccess__*` names via `--env-file`.
`.context/mimisbrunnr.env.controller` carries the `Parameters__api-read-token` /
`Parameters__api-write-token` names for the published controller's `--env-file`. The two `Parameters__*`
names are not valid shell identifiers, so they must not live in the sourceable file — separating the two
parser grammars is what keeps `set -a && source` from printing a token. The script also writes the
AppHost user secrets (`Parameters:api-read-token` / `Parameters:api-write-token`) so the AppHost path
injects the same values rather than generating its own.

Re-running without `--rotate` reuses the existing tokens. To regenerate:

```bash
scripts/provision-credentials.sh --rotate
```

**Restart the Host after `--rotate`.** The Host reads and hashes both tokens once at startup, so a running
Host — AppHost, direct run or container — keeps accepting the old tokens and answers `403` to the new
ones until it is restarted. Re-`source` the env file in any shell that runs the skills, too.

Options:

| Flag | Meaning |
|---|---|
| `--rotate` | Regenerate the tokens even if the file already exists |
| `--env-file PATH` | Write to a different path (default `.context/mimisbrunnr.env`). The path must be one git ignores — under `.context/`, or a name ending in `.env` — or the run is refused; see below |
| `--base-url URL` | The skill-side base URL (default `http://localhost:5141`). Must be a bare `http(s)://host[:port]` origin — anything else is refused, because the env file is `source`d. An existing custom value is kept across a re-split or `--rotate` |
| `--skip-apphost` | Do not write the AppHost user secrets. The bridge reads the AppHost `csproj` for its `UserSecretsId` and writes the user-secrets store directly, so it needs `python3` (it no longer invokes the .NET SDK at all) |
| `--allow-unignored-env-file` | Skip the refusal below, for a path this check cannot see as ignored — an untracked parent repository, say |

**The unignored-path refusal.** A token file is only safe from version control while something ignores
it, so the script asks git (`git check-ignore`) about both the `--env-file` and its `.controller`
sibling, and exits 1 with instructions if either is not ignored — a `git add .` would otherwise stage
live bearer tokens. A path git cannot answer for (outside the repository) is refused the same way;
`--allow-unignored-env-file` is the documented way past that, and the override is also right when an
external tool already keeps the file out of version control. With no repository at all
(`git rev-parse --git-dir` fails) the check does not apply, because there is no `git add .` to be
staged by.

## Run modes

### AppHost

```bash
scripts/provision-credentials.sh
set -a && source .context/mimisbrunnr.env && set +a   # exports CONTEXT_MEMORY_* for the skills
dotnet run --project src/SmoothAiProductContextMemory.AppHost
```

The script writes the AppHost user secrets (`Parameters:api-read-token` / `Parameters:api-write-token`),
so Aspire injects the same values the skills hold rather than generating per-session tokens of its own.
Without that bridge the AppHost would serve tokens the skills do not carry and every skill request
returns `403`. The dashboard-local `/login?t=…` URL is only for the dashboard UI, not the API
credentials. Add `--skip-apphost` only when you are **not** running the AppHost from this checkout
(for example, you run the published controller image and pass `--env-file` instead): the bridge needs
`python3`, not the .NET SDK, so skipping it on an SDK-less machine still leaves the AppHost on tokens
of its own and every skill request at `403`.

**User secrets load in Development only.** For the published `-apphost` controller (which runs in
Production, where user secrets are not loaded), pass `.context/mimisbrunnr.env.controller` via
`--env-file` instead — the provisioner writes the `Parameters__api-read-token` /
`Parameters__api-write-token` names into that controller env file, and that env-var spelling loads in
every environment. Without one of those two bridges the controller regenerates its own per-session
tokens and every skill request returns `403`.

### Controller container (no .NET toolchain)

```bash
scripts/provision-credentials.sh   # 1. write the API tokens the skills read
scripts/run.sh                     # 2. macOS, Linux
pwsh ./scripts/run.ps1             #    Windows / PowerShell 7
```

Step 2 pulls the published controller image and starts the whole stack in one command. Step 1 is the
same provisioner every other run mode on this page uses, and here it is not optional: the launcher
**adopts** that file's token pair rather than minting its own, so the controller and the skills hold the
same values. Without it the stack starts and every skill request returns `403` — `run.sh` also publishes
the client-facing values to `~/.mimisbrunnr/credentials`, but `run.ps1` writes only its own
`Parameters__*` file.

The launcher generates the five release secrets on first run — the three engine values
(`PostgresConfiguration__Password`, `BlobConfiguration__AccessKey`,
`BlobConfiguration__SecretKey`) that `docker.md` asks you to supply by hand — and mints the two
`Parameters__*` tokens only when the provisioner supplied neither. It keeps them in
`~/.mimisbrunnr/controller.env` (mode 600) so restarts do not rotate a token a running Host already
holds.

Your environment wins over the stored file, and a value supplied that way is deliberately not written
to disk. The token names contain hyphens, so they cannot be exported by a shell at all — use
`env 'Parameters__api-read-token=…' ./run.sh up` or PowerShell's `$env:` provider.

Options, the data-root layout, platform differences and the security notes are in
[`scripts/CONTROLLER_LAUNCHER.md`](../../scripts/CONTROLLER_LAUNCHER.md).

**Using the skills from another repository:** the store is a service started once per machine, and
`run.sh` also publishes the API credentials to `~/.mimisbrunnr/credentials` — outside every checkout. A
second repository therefore needs neither `provision-credentials.sh` nor a credential file of its own:
copy `.agents/skills/mimisbrunnr-*` across, and the clients find the credential on their own. Reads are
ambient; a write needs `set -a && source ~/.mimisbrunnr/credentials && set +a`, because a write
credential is deliberately never loaded implicitly. The full sequence, and what sharing one store means
for repository isolation, is in the README's *Use it in another repository*.

### Direct Host run

```bash
scripts/provision-credentials.sh
set -a && source .context/mimisbrunnr.env && set +a
dotnet run --project src/SmoothAiProductContextMemory.Host
```

The exported `ApiAccess__*` and `CONTEXT_MEMORY_*` names are both present, so the Host and the skills
work from one source.

### Bare container

The container reads the `ApiAccess__*` names via `--env-file` from the same file the skills `source` —
see [`docker.md`](docker.md) for the full form. Do not pass the tokens inline with `-e`; that lands the
secret in shell history and in any command that gets copied or pasted.

## The credential model

- Tokens are **operator-generated production credentials**, not development scaffolding. For a local,
  dockerised, externally-isolated host they are the one real credential in the system.
- The **fixed** credentials in the AppHost/container setup (the development Postgres/MinIO password and
  the `smooth-local` object-store key) are *not* this credential. They are development scaffolding for a
  local stack and are accepted on that basis; conflating the two — treating the bearer tokens as
  scaffolding too, or the fixed passwords as a real exposure — is the error this page exists to prevent.
- The API publishes a host port (`-p 127.0.0.1:5141:5141` in [`docker.md`](docker.md)), so "isolated" is a property of the operator's network
  configuration, not something the product enforces. Publishing beyond loopback, putting the stack on
  untrusted shared network infrastructure, or running on a laptop whose firewall permits inbound
  connections re-opens the transport-hardening / token-rotation / multi-tenancy concerns that are
  otherwise moot for this deployment target.

## Accepted states — recorded decisions, not open risks

For the MVP deployment target — **production configuration, local, dockerised, externally isolated** —
the following are **accepted**, deliberately, and are recorded here so successive review passes stop
re-raising them as findings.

- **Fixed development passwords in the AppHost and container setup are not a repository security
  issue.** The literal `LocalMachineAccessNoInterestingDataDev#Passw0rd!FirewallNotExposed` appears in
  several tracked files (docs, `AppHostConfiguration.cs`, the AppHost's `appsettings.Development.json`,
  the design-time `DbContextFactory`, and two test-framework files). On a host with no route to the
  outside world these are not secrets, and treating them as a finding buries the one credential that
  *is* real: the operator-generated bearer tokens.
- **The non-random object-store access key `smooth-local`** is accepted on the same basis. It is
  non-random in source and appears across the setup.
- **One password serves two services** — the same `DevelopmentPassword` is used for the PostgreSQL
  superuser and the MinIO root secret key, so a credential obtained from either yields the other.
  Accepted for an isolated stack; the reason it is acceptable is the isolation, not the reuse.

**The boundary that re-opens all three:** the deployment target stops being a locally isolated host —
publishing the API beyond loopback, putting the stack on untrusted shared network infrastructure, or
running it on a laptop whose firewall permits inbound connections. A decision to change the deployment
target must update this section in the same change.

**The distinction this section protects:** the *fixed* credentials above are accepted precisely because
they are development scaffolding for a local stack. The *bearer tokens* are operator-generated
production credentials for that same stack, which is why this page provisions them randomly, persists
them mode-600, and why they are **not** covered by this acceptance.
