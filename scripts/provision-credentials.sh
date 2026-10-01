#!/usr/bin/env bash
# One-command API credential provisioning for a local Mímisbrunnr deployment.
#
# Writes two distinct random Bearer tokens to gitignored env files that the service, the container
# controller and the host-side skills all read, bridging the AppHost path so Aspire injects the same
# values. Two files, three name forms, one source of truth:
#   - ApiAccess__ReadToken / ApiAccess__WriteToken   (what the Host's authorizer reads)
#   - Parameters__api-read-token / Parameters__api-write-token
#                                                   (the AppHost/container controller reads these —
#                                                    the env-var spelling of the same keys the
#                                                    user-secrets bridge below sets, and the only form
#                                                    that works in Production where user secrets are not
#                                                    loaded; usable as `--env-file` for the published
#                                                    controller)
#   - CONTEXT_MEMORY_READ_TOKEN / CONTEXT_MEMORY_WRITE_TOKEN / CONTEXT_MEMORY_BASE_URL
#                                                   (what the skills read)
# The values are identical across the two name forms; only the env var names differ.
#
# The two parsers disagree on identifier grammar, so they get two files:
#   - $ENV_FILE (default .context/mimisbrunnr.env) is the sourceable + standalone-Host `--env-file`
#     file. Every name is a valid shell identifier, so `set -a && source` exports CONTEXT_MEMORY_*
#     without error. It carries NO `Parameters__*` lines.
#   - ${ENV_FILE}.controller (default .context/mimisbrunnr.env.controller) is the published-controller
#     `--env-file`. The `Parameters__*` names contain hyphens and are not shell identifiers, so keeping
#     them here is what stops a `source` of the main file from printing a token as "command not found".
#
# The tokens are runtime configuration only: the Host hashes them at startup and never writes them to
# the store, so regenerating them invalidates no data. Deleting the files and re-running is a complete
# recovery procedure. See docs/wiki/setup.md.
#
# Run modes this makes work with the same tokens:
#   - Container: the Host reads the ApiAccess__* names from $ENV_FILE via `--env-file`.
#   - Direct Host run: the exported ApiAccess__* names are ordinary .NET configuration.
#   - AppHost: Aspire's `AddParameter("api-read-token", secret: true)` falls back to user secrets, so
#     the script writes `Parameters:api-read-token` / `Parameters:api-write-token` for the AppHost
#     project — making Aspire inject the same values the skills hold. Without this, the AppHost
#     generates its own per-session tokens and every skill request 403s.
#     User secrets load in **Development only**. For the published `-apphost` controller (Production),
#     the controller env file carries `Parameters__api-read-token` / `Parameters__api-write-token` (the
#     env-var spelling, which loads in every environment), so pass that file via `--env-file`.
#
# Usage:
#   scripts/provision-credentials.sh [--rotate] [--env-file PATH] [--base-url URL] [--skip-apphost]
#                                   [--allow-unignored-env-file]
#     --rotate                   regenerate the tokens even if the file already exists
#     --env-file                 write to this path (default .context/mimisbrunnr.env); a relative
#                                path is resolved against the caller's cwd
#     --base-url                 the skill-side base URL: a bare http(s) origin, anything else is
#                                refused (default: the file's existing value, else
#                                http://localhost:5141); naming a different one rewrites the pair
#                                keeping the tokens
#     --skip-apphost             do not write the AppHost user secrets; the bridge needs python3,
#                                not the .NET SDK (it reads the csproj with sed and writes the
#                                store directly), so this is only for a gitignored or absent env.
#                                With --rotate it warns if those secrets still hold the old pair
#     --allow-unignored-env-file skip the git-ignore refusal below, for a path this check cannot see
#                                as ignored
#
# Idempotent: re-running without --rotate reuses the existing tokens (re-asserting mode 600) and just
# reprints the export lines. Secrets must never be committed — *.env and .context/ are both gitignored.

set -euo pipefail

# Every file this script creates is created with the owner's-only bits already set, rather than
# under the caller's umask and narrowed afterwards. `chmod 600` after the fact leaves a window in
# which a file carrying bearer tokens is group- or world-readable — a window that is exactly as
# long as the write, and that no test can observe, because it has already closed by the time one
# runs.
umask 077

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APPHOST_PROJECT="${ROOT_DIR}/src/SmoothAiProductContextMemory.AppHost"
ENV_FILE="${ROOT_DIR}/.context/mimisbrunnr.env"
DEFAULT_BASE_URL="http://localhost:5141"
BASE_URL=""
BASE_URL_GIVEN=0
ROTATE=0
WRITE_APPHOST=1
ALLOW_UNIGNORED=0

# Temp files that may hold a token are removed on every exit path. A bare signal does not run an EXIT
# trap in every shell, so each one is converted to an `exit` that does.
TEMP_FILES=()
cleanup_temp_files() {
  local f
  for f in ${TEMP_FILES[@]+"${TEMP_FILES[@]}"}; do rm -f "$f"; done
}
trap cleanup_temp_files EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rotate) ROTATE=1; shift ;;
    --env-file) ENV_FILE="$2"; shift 2 ;;
    --base-url) BASE_URL="$2"; BASE_URL_GIVEN=1; shift 2 ;;
    --skip-apphost) WRITE_APPHOST=0; shift ;;
    --allow-unignored-env-file) ALLOW_UNIGNORED=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

# The base URL is written into a file that consumers `source`, so it is shell input: an unvalidated
# `--base-url 'http://x/$(touch pwned)'` would run on every source. It is held to a bare http(s)
# origin — host name, IPv4 or bracketed IPv6, optional port — whose character set contains nothing a
# shell assignment can expand. The line stays unquoted on purpose: the same file is the standalone
# Host's `docker run --env-file`, which keeps quote characters as part of the value.
BASE_URL_PATTERN='^https?://([A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?|\[[0-9A-Fa-f:.]+\])(:[0-9]{1,5})?/?$'
base_url_valid() {
  [[ "$1" =~ $BASE_URL_PATTERN ]]
}
if [[ "$BASE_URL_GIVEN" -eq 1 ]] && ! base_url_valid "$BASE_URL"; then
  echo "refusing --base-url: it must be a bare http(s) origin (scheme, host, optional port) with no" >&2
  echo "  credentials, path, query or shell metacharacters." >&2
  exit 2
fi

# Anchor a relative --env-file to the caller's working directory now, before anything reads it. The
# writes below resolve it against the caller's cwd, but the ignore check below runs from ROOT_DIR, so
# an unanchored path was asked about and written as two different files: git was asked whether
# `/repo/creds.env` is ignored, and the tokens landed in `./creds.env` — a live credential file under
# no ignore rule, which is exactly what the check below refuses. Name one path from here on.
[[ "$ENV_FILE" = /* ]] || ENV_FILE="$PWD/$ENV_FILE"

# The token file is only safe from version control while something ignores it. `*.env` and
# `*.env.controller` cover the defaults and `.context/` covers the default directory — but a
# caller-chosen `--env-file` whose name does not end in `.env` is covered by neither, while
# `docs/wiki/setup.md` states the file is gitignored regardless of where it points. Ask Git rather
# than pattern-matching its own ignore file, so nested and negated rules count the way they
# actually apply. The `.controller` sibling is checked separately: it is a distinct file, and a
# rule matching one does not match the other.
#
# `rev-parse --git-dir`, not a test for a `.git` directory: in a linked worktree `.git` is a
# *file*, so a `-d` test silently disables the whole check for every contributor who works in one
# — a guard that quietly does nothing is worse than no guard, because it reads as covered.
#
# `git check-ignore` also exits 128 for a path outside the repository, which is a refusal (nothing
# there is ignored), so the `!` below treats it the same as "not ignored" rather than as an error
# to be ignored.
if [[ "$ALLOW_UNIGNORED" -eq 0 ]] && git -C "$ROOT_DIR" rev-parse --git-dir >/dev/null 2>&1; then
  for candidate in "$ENV_FILE" "${ENV_FILE}.controller"; do
    if ! (cd "$ROOT_DIR" && git check-ignore -q "$candidate"); then
      echo "refusing to write credentials to ${candidate}: git does not ignore that path, so a" >&2
      echo "  'git add .' would stage live bearer tokens. Use a path under .context/ or ending" >&2
      echo "  in .env, add an ignore rule, or pass --allow-unignored-env-file if the path is" >&2
      echo "  already ignored by a means this check cannot see (an untracked parent repo, say)." >&2
      exit 1
    fi
  done
fi

mkdir -p "$(dirname "$ENV_FILE")"

# A pre-split file (written before the two-file layout) carries the `Parameters__*` names itself and has
# no `.controller` sibling, so the reuse branch below would leave it unsplit: `source` still prints a
# token and the published controller's `--env-file` does not exist. Treat that state as needing the
# rewrite, but NOT as a rotation: re-splitting must carry the file's existing token values forward, or
# an operator upgrading on the documented idempotent path would silently invalidate the tokens a running
# Host and its skills already hold. Only a missing file or an explicit --rotate regenerates. The grep
# runs only once the file is known to exist, so it cannot fail on a missing path under `set -e`.
needs_write=0
regenerate=0
if [[ ! -f "$ENV_FILE" || "$ROTATE" -eq 1 ]]; then
  needs_write=1
  regenerate=1
elif [[ ! -f "${ENV_FILE}.controller" ]] || grep -q '^Parameters__' "$ENV_FILE"; then
  needs_write=1   # re-split the pair, keeping the existing token values
fi

# The base URL is part of the file, not of the run: a re-split or a `--rotate` that does not name one
# keeps the file's own value rather than resetting a custom origin to the default. The kept value was
# written by an earlier run or by hand, so it is re-validated before it is written back. An explicit
# `--base-url` that differs from the file's value rewrites the pair, keeping the tokens.
rewrite_reason=""
existing_base=""
if [[ -f "$ENV_FILE" ]]; then
  existing_base="$(grep -m1 '^CONTEXT_MEMORY_BASE_URL=' "$ENV_FILE" | cut -d= -f2- || true)"
fi
if [[ "$BASE_URL_GIVEN" -eq 0 ]]; then
  if [[ -n "$existing_base" ]]; then
    if ! base_url_valid "$existing_base"; then
      echo "refusing to reuse ${ENV_FILE}: its CONTEXT_MEMORY_BASE_URL is not a bare http(s) origin." >&2
      echo "  Pass --base-url to replace it." >&2
      exit 1
    fi
    BASE_URL="$existing_base"
  else
    BASE_URL="$DEFAULT_BASE_URL"
  fi
elif [[ -f "$ENV_FILE" && "$BASE_URL" != "$existing_base" && "$needs_write" -eq 0 ]]; then
  needs_write=1
  rewrite_reason="base-url"
fi

if [[ "$regenerate" -eq 1 ]]; then
  READ_TOKEN="$(openssl rand -hex 32)"
  WRITE_TOKEN="$(openssl rand -hex 32)"
  if [[ "$READ_TOKEN" == "$WRITE_TOKEN" ]]; then
    echo "unexpected: generated tokens are identical" >&2
    exit 1
  fi

  # Sourceable + standalone-Host env file. Every name is a valid shell identifier so `set -a &&
  # source` exports CONTEXT_MEMORY_* without error; no `Parameters__*` line, which is what otherwise
  # makes a source print the token as a "command not found" value.
  cat > "$ENV_FILE" <<EOF
# Mímisbrunnr API credentials — generated $(date -u +%Y-%m-%dT%H:%M:%SZ). Not a secret worth
# protecting at rest beyond file permissions, but does not belong in version control.
# Sourceable (valid identifiers only) and usable as the standalone Host's \`--env-file\`.
ApiAccess__ReadToken=${READ_TOKEN}
ApiAccess__WriteToken=${WRITE_TOKEN}
CONTEXT_MEMORY_READ_TOKEN=${READ_TOKEN}
CONTEXT_MEMORY_WRITE_TOKEN=${WRITE_TOKEN}
CONTEXT_MEMORY_BASE_URL=${BASE_URL}
EOF
  chmod 600 "$ENV_FILE"

  # Published-controller env file: the `Parameters__*` names carry hyphens and are not shell
  # identifiers, so they live here, never in the sourceable file. Nothing else belongs here: the
  # controller gets the tokens through these two names, and every additional form is a second copy
  # of a bearer credential in a file whose only consumer reads two keys. The Host's `ApiAccess__*`
  # names stay in the sourceable file, which is the one the container is documented to read.
  cat > "${ENV_FILE}.controller" <<EOF
# Mímisbrunnr controller credentials — generated $(date -u +%Y-%m-%dT%H:%M:%SZ). Pass to the
# published \`-apphost\` controller via \`--env-file\`. The Parameters__* names are not shell
# identifiers, so this file is for a container env-file, never for \`source\`.
Parameters__api-read-token=${READ_TOKEN}
Parameters__api-write-token=${WRITE_TOKEN}
EOF
  chmod 600 "${ENV_FILE}.controller"
  echo "Wrote credentials to ${ENV_FILE} and ${ENV_FILE}.controller" >&2
else
  # Reuse, whether or not the pair is rewritten. The re-split branch above sets needs_write without
  # regenerate, and this branch is only reachable when the file exists, so the parse always has the
  # ApiAccess__* names to read.
  if [[ "$rewrite_reason" == "base-url" ]]; then
    echo "Rewriting the base URL in ${ENV_FILE}, keeping the existing tokens" >&2
  elif [[ "$needs_write" -eq 1 ]]; then
    echo "Re-splitting the credential pair in ${ENV_FILE}, keeping the existing tokens" >&2
  else
    echo "Reusing existing credentials in ${ENV_FILE}" >&2
  fi
  # A redirect rewrites a file's contents but not its mode, and an operator's copy or editor may have
  # widened an existing file, so owner-only is re-asserted on every reuse rather than only on create.
  chmod 600 "$ENV_FILE"
  [[ ! -f "${ENV_FILE}.controller" ]] || chmod 600 "${ENV_FILE}.controller"
  # `|| true` on both greps: under `set -e` a missing line aborts the assignment silently, and an env
  # file with no `ApiAccess__*` name is exactly the truncated case the check below refuses loudly.
  READ_TOKEN="$(grep -m1 '^ApiAccess__ReadToken=' "$ENV_FILE" | cut -d= -f2- || true)"
  WRITE_TOKEN="$(grep -m1 '^ApiAccess__WriteToken=' "$ENV_FILE" | cut -d= -f2- || true)"
  # A reuse path that does not check what it parsed will happily rewrite a truncated, hand-edited
  # or half-written file *keeping the broken value*, and then write that same broken value into the
  # controller file and the AppHost user secrets — turning a corrupt file into a confidently
  # propagated one. The tokens are `openssl rand -hex 32`, so the shape is exactly checkable; refuse
  # rather than guess, because silently regenerating would invalidate a token a running Host holds
  # and the operator would not be told which of the two happened.
  for pair in "ApiAccess__ReadToken=$READ_TOKEN" "ApiAccess__WriteToken=$WRITE_TOKEN"; do
    if [[ ! "${pair#*=}" =~ ^[0-9a-f]{64}$ ]]; then
      echo "refusing to reuse ${ENV_FILE}: ${pair%%=*} is not a 64-character hex token." >&2
      echo "  The file is truncated, hand-edited or written by another tool. Repair it, or pass" >&2
      echo "  --rotate to mint a new pair (which invalidates the tokens a running Host holds)." >&2
      exit 1
    fi
  done
  if [[ "$READ_TOKEN" == "$WRITE_TOKEN" ]]; then
    echo "refusing to reuse ${ENV_FILE}: the read and write tokens are identical, so the read" >&2
    echo "  capability would be the write capability. Pass --rotate to mint a new pair." >&2
    exit 1
  fi
fi

# Re-split: the pair is inconsistent (no .controller sibling, or Parameters__* still in the sourceable
# file) but the tokens are good, so rewrite both files from the parsed values. This must be a separate
# step from the generate branch above — gating it on `regenerate` reported "Re-splitting" and printed
# the controller path while writing nothing, so an upgrading operator kept a sourceable file that still
# printed a token and had no controller env file at all.
if [[ "$needs_write" -eq 1 && "$regenerate" -eq 0 ]]; then
  cat > "${ENV_FILE}" <<EOF
# Mímisbrunnr API credentials — rewritten $(date -u +%Y-%m-%dT%H:%M:%SZ), token values unchanged.
# Sourceable (valid identifiers only) and usable as the standalone Host's \`--env-file\`.
ApiAccess__ReadToken=${READ_TOKEN}
ApiAccess__WriteToken=${WRITE_TOKEN}
CONTEXT_MEMORY_READ_TOKEN=${READ_TOKEN}
CONTEXT_MEMORY_WRITE_TOKEN=${WRITE_TOKEN}
CONTEXT_MEMORY_BASE_URL=${BASE_URL}
EOF
  chmod 600 "$ENV_FILE"
  cat > "${ENV_FILE}.controller" <<EOF
# Mímisbrunnr controller credentials — rewritten $(date -u +%Y-%m-%dT%H:%M:%SZ). Pass to the
# published \`-apphost\` controller via \`--env-file\`. The Parameters__* names are not shell
# identifiers, so this file is for a container env-file, never for \`source\`.
Parameters__api-read-token=${READ_TOKEN}
Parameters__api-write-token=${WRITE_TOKEN}
EOF
  chmod 600 "${ENV_FILE}.controller"
fi

# The AppHost user-secrets store for this project, or nothing when the project or its id is absent.
apphost_secrets_file() {
  local csproj="${APPHOST_PROJECT}/SmoothAiProductContextMemory.AppHost.csproj" id
  [[ -f "$csproj" ]] || return 0
  id="$(sed -n 's:.*<UserSecretsId>\(.*\)</UserSecretsId>.*:\1:p' "$csproj" | head -1)"
  [[ -n "$id" ]] || return 0
  if [[ -n "${APPDATA:-}" ]]; then
    printf '%s\n' "${APPDATA}/Microsoft/UserSecrets/${id}/secrets.json"
  else
    printf '%s\n' "${HOME}/.microsoft/usersecrets/${id}/secrets.json"
  fi
}

if [[ "$WRITE_APPHOST" -eq 1 ]]; then
  if [[ -f "${APPHOST_PROJECT}/SmoothAiProductContextMemory.AppHost.csproj" ]]; then
    # `dotnet user-secrets set NAME VALUE` takes the value as an argv element, which is readable in
    # `ps` by every user on the host for the lifetime of the process and lands in any shell trace
    # that is recording. The SDK offers no stdin form, so the store is written directly instead:
    # the same flat {"name": "value"} JSON the command itself produces, merged into whatever is
    # already there so an unrelated secret is not clobbered, and replaced atomically so a
    # concurrent reader sees either the old file or the new one, never a half-written one.
    write_apphost_secrets() {
      local target dir tmp
      target="$(apphost_secrets_file)"
      if [[ -z "$target" ]]; then
        echo "warning: no <UserSecretsId> in the AppHost project; skipping the user-secrets bridge" >&2
        return 0
      fi
      dir="$(dirname "$target")"
      mkdir -p "$dir"
      chmod 700 "$dir" 2>/dev/null || true
      tmp="$(mktemp "${dir}/.secrets.XXXXXX")"
      TEMP_FILES+=("$tmp")
      SECRETS_TARGET="$target" SECRETS_TMP="$tmp" \
      SECRETS_READ="$READ_TOKEN" SECRETS_WRITE="$WRITE_TOKEN" \
        python3 - <<'PY'
import json
import os

target = os.environ["SECRETS_TARGET"]
merged = {}
if os.path.exists(target):
    try:
        with open(target, encoding="utf-8-sig") as handle:
            existing = json.load(handle)
    except ValueError:
        existing = None
    if isinstance(existing, dict):
        for key, value in existing.items():
            # Accept both shapes the SDK has written: flat, and the nested {Type,Value} object.
            merged[key] = value.get("Value") if isinstance(value, dict) and "Value" in value else value
merged["Parameters:api-read-token"] = os.environ["SECRETS_READ"]
merged["Parameters:api-write-token"] = os.environ["SECRETS_WRITE"]
with open(os.environ["SECRETS_TMP"], "w", encoding="utf-8") as handle:
    json.dump(merged, handle, indent=2)
    handle.write("\n")
PY
      mv "$tmp" "$target"
    }
    write_apphost_secrets
    echo "Wrote AppHost user secrets (Parameters:api-read-token / api-write-token)." >&2
    echo "NOTE: user secrets load in Development only. For the published controller (Production), pass" >&2
    echo "${ENV_FILE}.controller via --env-file (it carries Parameters__api-read-token / __api-write-token)." >&2
  else
    echo "warning: AppHost project not found at ${APPHOST_PROJECT}; skipping AppHost bridge" >&2
  fi
elif [[ "$regenerate" -eq 1 ]]; then
  # New tokens with the bridge skipped leave any earlier AppHost user secrets holding the old pair, and
  # Aspire injects those into the Host — so an AppHost run 403s every skill request with no hint why.
  # Only the key names are checked; the stored values are never read into this shell.
  stale_secrets="$(apphost_secrets_file)"
  if [[ -n "$stale_secrets" && -f "$stale_secrets" ]] \
      && grep -qE '"Parameters:api-(read|write)-token"' "$stale_secrets"; then
    echo "" >&2
    echo "WARNING: new tokens were minted with --skip-apphost, but the AppHost user secrets at" >&2
    echo "  ${stale_secrets}" >&2
    echo "  still hold the PREVIOUS Parameters:api-read-token / api-write-token. An AppHost run will" >&2
    echo "  inject those stale tokens and every skill request will 403. Re-run without --skip-apphost" >&2
    echo "  to update them, or remove those two keys from that file." >&2
    echo "" >&2
  fi
fi

# Sourceable export lines for the operator's shell: the file declares the CONTEXT_MEMORY_* names as
# literal assignments, so `set -a` before sourcing exports them for the skills. The standalone Host
# reads the ApiAccess__* names from the same file via --env-file; the published controller reads the
# Parameters__* names from ${ENV_FILE}.controller (see docs/wiki/docker.md).
echo "# To export the skill-side credentials, source the file:"
echo "set -a && source ${ENV_FILE} && set +a"
echo "# (The standalone Host reads the same file's ApiAccess__* names via --env-file; the published"
echo "#  controller reads the Parameters__* names from ${ENV_FILE}.controller; see docs/wiki/docker.md)"
