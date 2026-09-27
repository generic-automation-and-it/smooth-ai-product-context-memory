#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
cp "$repo_root/.github/scripts/opencode-credential-guard.sh" "$scratch/opencode"
cp "$repo_root/.github/scripts/harden-opencode-config.py" "$scratch/harden-opencode-config.py"
cat > "$scratch/opencode.real" <<'FAKE'
#!/usr/bin/env bash
if [ "${1:-}" = --version ]; then echo 2.0.12; exit 0; fi
if [ "${1:-}" = debug ] && [ "${2:-}" = config ]; then
  printf '[{"type":"document","path":"%s/.config/opencode/opencode.json"}]\n' "$HOME"
  exit 0
fi
env | cut -d= -f1 | sort > "$2"
FAKE
chmod +x "$scratch/opencode" "$scratch/opencode.real"
cat > "$scratch/config.json" <<'JSON'
{"agent":{"review":{"permission":{"read":"allow","grep":"allow"},"tools":{}},"analyse":{"permission":{"read":"allow","edit":"allow","grep":"allow"},"tools":{}}}}
JSON

(
  export HOME="$scratch/model-home"
  export GITHUB_TOKEN=synthetic-github GH_TOKEN=synthetic-gh
  export OPENCODE_OPENAI_API_KEY=synthetic-openai
  export OPENCODE_GO_OPENAI_API_KEY=synthetic-go
  export OPENCODE_ANTHROPIC_API_KEY=synthetic-unused
  export AWS_SECRET_ACCESS_KEY=synthetic-aws
  export OPENCODE_REVIEW_REPORT_PROVIDER=OPENAI
  export OPENCODE_ANALYSE_PROVIDER=OPENCODE-GO-OPENAI
  export OPENCODE_CONFIG="$scratch/config.json"
  export OPENCODE_CONFIG_CONTENT='{"agent":{"review":{"permission":{"read":"allow"}}}}'
  export OPENCODE_CONFIG_DIR="$scratch/untrusted"
  export HTTPS_PROXY='https://synthetic:proxy@example.invalid:8080'
  "$scratch/opencode" capture "$scratch/child-env"
  [ "$GITHUB_TOKEN" = synthetic-github ] # parent subshell unchanged
)
cmp "$scratch/config.json" "$scratch/model-home/.config/opencode/opencode.json"
HOME="$scratch/model-home" OPENCODE_CONFIG="$scratch/config.json" \
  "$scratch/opencode" debug config | jq -e --arg p "$scratch/config.json" \
  'any(.[]; .type == "document" and .path == $p)' >/dev/null

for name in OPENCODE_OPENAI_API_KEY OPENCODE_GO_OPENAI_API_KEY OPENCODE_CONFIG; do
  grep -Fxq "$name" "$scratch/child-env"
done
for name in GITHUB_TOKEN GH_TOKEN OPENCODE_ANTHROPIC_API_KEY AWS_SECRET_ACCESS_KEY HTTPS_PROXY OPENCODE_CONFIG_CONTENT OPENCODE_CONFIG_DIR; do
  if grep -Fxq "$name" "$scratch/child-env"; then
    echo "unexpected credential reached OpenCode: $name" >&2
    exit 1
  fi
done

python3 - "$scratch/config.json" <<'PY'
import json, sys
data = json.load(open(sys.argv[1], encoding="utf-8"))
for name in ("review", "analyse"):
    agent = data["agent"][name]
    assert agent["permission"]["read"]["*.git/*"] == "deny"
    assert agent["permission"]["read"]["*.env.*"] == "deny"
    assert agent["permission"]["grep"] == "deny"
    assert agent["tools"]["grep"] is False
assert data["agent"]["analyse"]["permission"]["edit"]["*.git/*"] == "deny"
assert data["agent"]["analyse"]["permission"]["external_directory"] == "deny"
PY

printf '{"agents":{"review":{"permissions":[]},"analyse":{"permissions":[]}}}\n' > "$scratch/native.json"
python3 "$repo_root/.github/scripts/harden-opencode-config.py" "$scratch/native.json"
python3 - "$scratch/native.json" <<'PY'
import json, sys
agents = json.load(open(sys.argv[1], encoding="utf-8"))["agents"]
for name in ("review", "analyse"):
    rules = agents[name]["permissions"]
    assert {"action": "grep", "resource": "*", "effect": "deny"} in rules
    assert {"action": "read", "resource": "*.env", "effect": "deny"} in rules
assert {"action": "edit", "resource": "*.git/*", "effect": "deny"} in agents["analyse"]["permissions"]
PY

if OPENCODE_REVIEW_REPORT_PROVIDER=UNKNOWN "$scratch/opencode" --version >/dev/null 2>&1; then
  echo 'unknown provider was not rejected' >&2
  exit 1
fi

mkdir -p "$scratch/project/nested"
(
  cd "$scratch/project/nested"
  GITHUB_WORKSPACE="$scratch/project" "$scratch/opencode" capture "$scratch/safe-env"
)
printf '{"agent":{}}\n' > "$scratch/project/opencode.json"
if (cd "$scratch/project/nested" && GITHUB_WORKSPACE="$scratch/project" \
    "$scratch/opencode" --version >/dev/null 2>&1); then
  echo 'project config override was not rejected' >&2
  exit 1
fi

cat > "$scratch/installer" <<'FAKE_INSTALL'
#!/usr/bin/env bash
mkdir -p "$HOME/.opencode/bin"
cat > "$HOME/.opencode/bin/opencode" <<'FAKE_BIN'
#!/usr/bin/env bash
if [ "${1:-}" = --version ]; then echo 2.0.12; fi
FAKE_BIN
chmod +x "$HOME/.opencode/bin/opencode"
FAKE_INSTALL
HOME="$scratch/home" GITHUB_PATH="$scratch/github-path" \
  bash "$repo_root/.github/scripts/install-guarded-opencode.sh" "$scratch/installer" >/dev/null
test -x "$scratch/home/.opencode/bin/opencode.real"
test -f "$scratch/home/.opencode/bin/harden-opencode-config.py"

helper="$repo_root/.github/scripts/git-credential-from-env.sh"
credential="$(printf 'protocol=https\nhost=github.com\n\n' | \
  GH_TOKEN=synthetic-test-token GIT_TERMINAL_PROMPT=0 \
  git -c credential.helper= -c "credential.helper=!bash $helper" credential fill)"
[ "$(printf '%s\n' "$credential" | sed -n 's/^password=//p')" = synthetic-test-token ]
if printf 'protocol=https\nhost=example.invalid\n\n' | \
   GH_TOKEN=synthetic-test-token bash "$helper" get >/dev/null 2>&1; then
  echo 'Git helper accepted an unexpected host' >&2
  exit 1
fi

git init -q "$scratch/hook-repo"
git -C "$scratch/hook-repo" config user.email test@example.invalid
git -C "$scratch/hook-repo" config user.name Test
printf 'x\n' > "$scratch/hook-repo/file"
git -C "$scratch/hook-repo" add file
cat > "$scratch/hook-repo/.git/hooks/pre-commit" <<HOOK
#!/usr/bin/env bash
touch "$scratch/hook-fired"
HOOK
chmod +x "$scratch/hook-repo/.git/hooks/pre-commit"
GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=core.hooksPath GIT_CONFIG_VALUE_0=/dev/null \
  git -C "$scratch/hook-repo" commit -qm 'test: disable hooks'
test ! -e "$scratch/hook-fired"

echo 'CI credential guard, helper, and hook protection passed'
