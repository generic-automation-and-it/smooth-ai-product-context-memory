#!/usr/bin/env bash
# OpenCode entrypoint for the CI review and auto-fix jobs. The parent scripts
# need GitHub and provider credentials; a tool-using model does not.
set -euo pipefail

real_bin="$(dirname "${BASH_SOURCE[0]}")/opencode.real"
[ -x "$real_bin" ] || { echo 'guarded OpenCode binary is missing' >&2; exit 127; }

# OpenCode v2 still discovers project-local config and plugins even when
# OPENCODE_DISABLE_PROJECT_CONFIG=1. Those files outrank OPENCODE_CONFIG and
# plugins execute code before an agent's tool permissions can help. Fail closed
# if a PR adds one on the path from the invocation directory to the checkout.
if [ -n "${GITHUB_WORKSPACE:-}" ]; then
  scan_dir="$(pwd -P)"
  workspace="$(cd "$GITHUB_WORKSPACE" && pwd -P)"
  case "$scan_dir/" in
    "$workspace/"|"$workspace/"*) ;;
    *) echo 'OpenCode invocation is outside the checkout' >&2; exit 65 ;;
  esac
  while :; do
    for candidate in "$scan_dir/opencode.json" "$scan_dir/opencode.jsonc" "$scan_dir/.opencode"; do
      if [ -e "$candidate" ] || [ -L "$candidate" ]; then
        echo "untrusted project OpenCode config is not allowed in CI: $candidate" >&2
        exit 65
      fi
    done
    [ "$scan_dir" = "$workspace" ] && break
    scan_dir="$(dirname "$scan_dir")"
  done
fi

declare -A needed_keys=()
add_provider_key() {
  local key_name
  case "$1" in
    GEMINI) key_name=OPENCODE_GEMINI_API_KEY ;;
    COPILOT) key_name=OPENCODE_COPILOT_API_KEY ;;
    OPENAI) key_name=OPENCODE_OPENAI_API_KEY ;;
    ANTHROPIC) key_name=OPENCODE_ANTHROPIC_API_KEY ;;
    OPENCODE-GO-OPENAI|OPENCODE-GO-RESPONSES) key_name=OPENCODE_GO_OPENAI_API_KEY ;;
    OPENCODE-GO-ANTHROPIC) key_name=OPENCODE_GO_ANTHROPIC_API_KEY ;;
    OPEN_ROUTER) key_name=OPENCODE_OPENROUTER_API_KEY ;;
    '') return ;;
    *) echo "unsupported OpenCode provider selector: $1" >&2; exit 64 ;;
  esac
  needed_keys["$key_name"]=1
}

# The v2 background service starts during the health check, before individual
# model calls. It must retain the review fallback key and, when different, the
# analyse primary key for that service's lifetime. No other provider key enters.
add_provider_key "${OPENCODE_REVIEW_REPORT_PROVIDER:-}"
add_provider_key "${OPENCODE_ANALYSE_PROVIDER:-}"

while IFS= read -r name; do
  if [[ -n "${needed_keys[$name]:-}" ]]; then
    continue
  fi
  case "$name" in
    PATH|HOME|USER|LOGNAME|SHELL|LANG|LC_*|TMPDIR|TMP|TEMP|CI|GITHUB_ACTIONS|RUNNER_TEMP|NODE_EXTRA_CA_CERTS|SSL_CERT_FILE|SSL_CERT_DIR|CURL_CA_BUNDLE)
      ;;
    HTTP_PROXY|HTTPS_PROXY|ALL_PROXY|http_proxy|https_proxy|all_proxy)
      # Proxy userinfo is itself a credential; fail closed on that transport.
      if [[ "${!name}" =~ ^[[:alpha:]][[:alnum:]+.-]*://[^/@]+@ ]]; then unset "$name"; fi
      ;;
    NO_PROXY|no_proxy)
      ;;
    OPENCODE_CONFIG|OPENCODE_AGENT|OPENCODE_DISABLE_CLAUDE_CODE)
      ;;
    OPENCODE_*)
      # Only explicit CLI controls survive. This excludes future/unknown
      # OPENCODE_* credentials and config-injection variables by default.
      unset "$name"
      ;;
    *) unset "$name" ;;
  esac
done < <(compgen -e)

if [ -n "${OPENCODE_CONFIG:-}" ]; then
  [ -f "$OPENCODE_CONFIG" ] || { echo 'OpenCode config is missing' >&2; exit 66; }
  python3 "$(dirname "${BASH_SOURCE[0]}")/harden-opencode-config.py" "$OPENCODE_CONFIG"
  # OpenCode v2 currently ignores OPENCODE_CONFIG when choosing config files.
  # The runner is ephemeral; use its global config path as the effective source
  # after preserving the upstream-resolved config and provider URL injections.
  config_dir="$HOME/.config/opencode"
  mkdir -p "$config_dir"
  global_config="$config_dir/opencode.json"
  if [ -e "$global_config" ] || [ -L "$global_config" ]; then
    [ -f "$global_config" ] && [ ! -L "$global_config" ] && [ -f "$config_dir/.ci-managed" ] || {
      echo 'refusing to overwrite an unmanaged OpenCode global config' >&2
      exit 66
    }
  fi
  install -m 0600 "$OPENCODE_CONFIG" "$global_config"
  : > "$config_dir/.ci-managed"
fi

if [ "${1:-}" = debug ] && [ "${2:-}" = config ] && [ -n "${OPENCODE_CONFIG:-}" ]; then
  # Upstream opencode-health.sh checks for the resolved path in debug config.
  # V2 ignores that env path, so report it as an alias only after the real CLI
  # confirms it loaded the byte-identical global file. A foreign service or a
  # config mismatch still fails the upstream binding check.
  actual_sources="$("$real_bin" "$@")"
  cmp -s "$OPENCODE_CONFIG" "$global_config" || exit 66
  printf '%s\n' "$actual_sources" | jq -e --arg p "$global_config" \
    'any(.[]; .type == "document" and .path == $p)' >/dev/null || exit 66
  printf '%s\n' "$actual_sources" | jq --arg p "$OPENCODE_CONFIG" \
    '. + [{"type":"document","path":$p}]'
  exit
fi

exec "$real_bin" "$@"
