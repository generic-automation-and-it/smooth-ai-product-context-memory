#!/usr/bin/env bash
# One-shot Git credential helper. The selected token is in GH_TOKEN, never argv,
# a remote URL, or git config. Refuse every host except github.com.
set -euo pipefail

[ "${1:-}" = get ] || exit 0
protocol='' host=''
while IFS='=' read -r key value; do
  [ -n "$key" ] || break
  case "$key" in
    protocol) protocol="$value" ;;
    host) host="$value" ;;
  esac
done

[ "$protocol" = https ] && [ "$host" = github.com ] && [ -n "${GH_TOKEN:-}" ] || exit 1
printf 'username=x-access-token\npassword=%s\n' "$GH_TOKEN"
