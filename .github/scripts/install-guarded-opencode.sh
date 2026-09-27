#!/usr/bin/env bash
# Install the upstream v2 CLI first, then replace its launcher with a guard.
# The real binary remains adjacent as opencode.real; upstream scripts can keep
# calling `opencode` without any changes to their provider/fallback plumbing.
set -euo pipefail

installer="${1:?upstream install-opencode.sh path required}"
bash "$installer"

bin_dir="$HOME/.opencode/bin"
mkdir -p "$bin_dir"
launcher="$bin_dir/opencode"
if [ -x "$launcher" ]; then
  real_bin="$launcher"
else
  real_bin="$(command -v opencode)"
fi

if [ "$real_bin" = "$launcher" ]; then
  mv "$launcher" "$bin_dir/opencode.real"
else
  ln -s "$real_bin" "$bin_dir/opencode.real"
fi
install -m 0755 "$(dirname "${BASH_SOURCE[0]}")/opencode-credential-guard.sh" "$launcher"
install -m 0644 "$(dirname "${BASH_SOURCE[0]}")/harden-opencode-config.py" "$bin_dir/harden-opencode-config.py"

# Later Actions steps prepend GITHUB_PATH entries. The upstream review script
# also prepends this exact directory, so neither path can bypass the guard.
echo "$bin_dir" >> "${GITHUB_PATH:-/dev/null}"
"$launcher" --version >/dev/null
