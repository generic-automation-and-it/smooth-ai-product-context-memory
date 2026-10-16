#!/usr/bin/env bash
# Shared Claude Code / Codex UserPromptSubmit router for the create-worktask skill.
# Only an imperative opening triggers it; questions and explicit skill calls do not.

set -euo pipefail

prompt=$(jq -r '.prompt // empty' 2>/dev/null) || exit 0
[ -n "$prompt" ] || exit 0

# Ignore leading blank lines, then inspect only the first request line. The skill
# description still handles less formulaic requests through normal discovery.
first_line=$(printf '%s\n' "$prompt" | sed -n '/[^[:space:]]/{p;q;}')

if printf '%s\n' "$first_line" | grep -Eiq \
    '^[[:space:]]*(please[[:space:]]+)?(create|make)[[:space:]]+(a[[:space:]]+)?work([[:space:]]|-)?task([^[:alnum:]_-]|$)'; then
    cat <<'EOF'
The user requested a worktask. Invoke the create-worktask skill for this turn: load .agents/skills/create-worktask/SKILL.md and follow its current workflow. This hook only routes the request; do not execute the resulting worktask in the same turn.
EOF
fi
