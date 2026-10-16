#!/usr/bin/env bash
# Skill: create-hld
# Prints the repository's AGENTS.md quality rule, then the HLD-only delta the
# rule does not state. The rule is the single source; this script carries no copy.

set -euo pipefail

REPO_ROOT=$(git rev-parse --show-toplevel 2>/dev/null || { cd "$(dirname "$0")/../../../.." && pwd; })
RULE="$REPO_ROOT/.agents/rules/meta/knowledge-conventional-contexts-quality.instructions.md"

if [ ! -f "$RULE" ]; then
    echo "hld-agents-rules.sh: AGENTS.md quality rule not found at $RULE" >&2
    exit 1
fi

cat "$RULE"

cat <<'DELTA'

## HLD delta (create-hld)

Apply the rule's *Design-Documentation Folders* section. In addition, an HLD AGENTS.md carries:
- No code or code snippets — those live in examples/ only.
- No implementation plan, phasing or execution sequencing — tracked in the issue/work tracker.
DELTA
