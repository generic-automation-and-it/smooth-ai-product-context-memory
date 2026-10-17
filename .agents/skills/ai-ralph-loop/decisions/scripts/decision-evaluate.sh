#!/bin/bash
# decision-evaluate.sh — Evaluate a code change via OpenJEV decision API.
#
# Usage:
#   decision-evaluate.sh --category <cat> --before <file> --after <file> --context <ctx>
#
# Arguments:
#   --category   Ralph loop category (feature-gap, potential-bug, architectural-issue,
#                yagni, kiss, clean-code)
#   --before     Path to file containing the BEFORE code (or "-" for stdin)
#   --after      Path to file containing the AFTER code (or "-" for stdin)
#   --context    File:line context string (e.g. "src/Foo.cs:42")
#
# Output (stdout, pipe-delimited):
#   value=clear_value|pct=72|keep=true|confidence=0.35
#
# Exit codes:
#   0  Success (decision made, or fallback after all attempts exhausted)
#   1  Usage error
#
# Environment variables (all optional, set by installer):
#   SMOOTH_DEVEX_DECISIONS_ENABLED       Must be "true" to run. Otherwise outputs fallback.
#   SMOOTH_DEVEX_DECISIONS_BASE_URL      OpenJEV base URL (default: http://127.0.0.1:1234)
#   SMOOTH_DEVEX_DECISIONS_PATH          API path (default: /v1/systemone)
#   SMOOTH_DEVEX_DECISIONS_MODEL         Model name (default: openjev)
#   SMOOTH_DEVEX_DECISIONS_MIN_PROBABILITY  Threshold 0-1 (default: 0.60)
#   SMOOTH_DEVEX_DECISIONS_MAX_ATTEMPTS  Retry count (default: 3)
#   SMOOTH_DEVEX_DECISIONS_TIMEOUT       Curl timeout seconds (default: 60)
#
# Fallback behavior: on timeout, connection error, or parse failure, retries up to
# MAX_ATTEMPTS. If all attempts fail, outputs "fallback" line — the caller (ralph loop)
# proceeds without a decision gate, same as when decisions are disabled.

set -euo pipefail

CATEGORY=""
BEFORE_FILE=""
AFTER_FILE=""
CONTEXT_STR=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --category)  CATEGORY="$2"; shift 2 ;;
    --before)    BEFORE_FILE="$2"; shift 2 ;;
    --after)     AFTER_FILE="$2"; shift 2 ;;
    --context)   CONTEXT_STR="$2"; shift 2 ;;
    *) echo "decision-evaluate: unknown option: $1" >&2; exit 1 ;;
  esac
done

for var in CATEGORY BEFORE_FILE AFTER_FILE CONTEXT_STR; do
  if [ -z "${!var}" ]; then
    echo "decision-evaluate: missing required option --$(echo "$var" | tr '_' '-' | tr 'A-Z' 'a-z')" >&2
    exit 1
  fi
done

# --- Gate check: skip when decisions not enabled ---
if [ "${SMOOTH_DEVEX_DECISIONS_ENABLED:-}" != "true" ]; then
  echo "value=disabled|pct=0|keep=false|confidence=0|fallback=true"
  exit 0
fi

# --- Resolve paths ---
BASE_URL="${SMOOTH_DEVEX_DECISIONS_BASE_URL:-http://127.0.0.1:1234}"
API_PATH="${SMOOTH_DEVEX_DECISIONS_PATH:-/v1/systemone}"
MODEL="${SMOOTH_DEVEX_DECISIONS_MODEL:-openjev}"
MIN_PROB="${SMOOTH_DEVEX_DECISIONS_MIN_PROBABILITY:-0.60}"
MAX_ATTEMPTS="${SMOOTH_DEVEX_DECISIONS_MAX_ATTEMPTS:-3}"
TIMEOUT="${SMOOTH_DEVEX_DECISIONS_TIMEOUT:-60}"

# Template lives next to this script's parent: decisions/templates/build.json
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE_FILE="${SMOOTH_DEVEX_DECISIONS_TEMPLATE:-$SCRIPT_DIR/../templates/build.json}"

if [ ! -f "$TEMPLATE_FILE" ]; then
  echo "decision-evaluate: template not found: $TEMPLATE_FILE" >&2
  echo "value=error|pct=0|keep=false|confidence=0|fallback=true"
  exit 0
fi

# Read before/after code
if [ "$BEFORE_FILE" = "-" ]; then
  BEFORE_CODE="$(cat)"
else
  [ -f "$BEFORE_FILE" ] || { echo "decision-evaluate: before file not found: $BEFORE_FILE" >&2; echo "value=error|pct=0|keep=false|confidence=0|fallback=true"; exit 0; }
  BEFORE_CODE="$(cat "$BEFORE_FILE")"
fi

if [ "$AFTER_FILE" = "-" ]; then
  AFTER_CODE="$(cat)"
else
  [ -f "$AFTER_FILE" ] || { echo "decision-evaluate: after file not found: $AFTER_FILE" >&2; echo "value=error|pct=0|keep=false|confidence=0|fallback=true"; exit 0; }
  AFTER_CODE="$(cat "$AFTER_FILE")"
fi

URL="${BASE_URL}${API_PATH}"

# --- Build request payload + send + parse, all in one python3 invocation ---
# Retries happen inside python so we don't fork a new process per attempt.
python3 - "$TEMPLATE_FILE" "$BEFORE_CODE" "$AFTER_CODE" "$CONTEXT_STR" "$CATEGORY" "$URL" "$MODEL" "$MAX_ATTEMPTS" "$TIMEOUT" "$MIN_PROB" <<'PYEOF'
import json, sys, urllib.request, urllib.error

template_file = sys.argv[1]
before_code   = sys.argv[2]
after_code    = sys.argv[3]
context_str   = sys.argv[4]
category      = sys.argv[5]
url           = sys.argv[6]
model         = sys.argv[7]
max_attempts  = int(sys.argv[8])
timeout       = int(sys.argv[9])
min_prob      = float(sys.argv[10])

with open(template_file) as f:
    template = json.load(f)

# Substitute placeholders in state string
state = template["state"]
state = state.replace("{{BEFORE}}", before_code)
state = state.replace("{{AFTER}}", after_code)
state = state.replace("{{CONTEXT}}", context_str)
state = state.replace("{{CATEGORY}}", category)

payload = {
    "model": model,
    "state": state,
    "questions": template["questions"]
}

data = json.dumps(payload).encode("utf-8")

def validate_response(response_text):
    """Validate response structure and required fields. Returns parsed dict or None."""
    if not response_text:
        return None
    try:
        result = json.loads(response_text)
    except (json.JSONDecodeError, ValueError):
        return None

    answers = result.get("answers")
    if not isinstance(answers, dict):
        return None

    # Detect template mode and validate required fields
    if "developer_value" in answers:
        # Code mode: validate developer_value structure
        dev = answers.get("developer_value")
        if not isinstance(dev, dict):
            return None
        probs = dev.get("probabilities")
        if not isinstance(probs, dict):
            return None
        # Check that probability values are numeric
        for key in ["closes_gap", "improves_quality"]:
            val = probs.get(key)
            if val is not None and not isinstance(val, (int, float)):
                return None
        return result

    elif "architectural_value" in answers:
        # Plan mode: validate architectural_value structure
        arch = answers.get("architectural_value")
        if not isinstance(arch, dict):
            return None
        probs = arch.get("probabilities")
        if not isinstance(probs, dict):
            return None
        # Check that probability values are numeric
        for key in ["2", "3"]:
            val = probs.get(key)
            if val is not None and not isinstance(val, (int, float)):
                return None
        return result

    return None

parsed_result = None
for attempt in range(1, max_attempts + 1):
    try:
        req = urllib.request.Request(
            url,
            data=data,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            response_text = resp.read().decode("utf-8")
        parsed_result = validate_response(response_text)
        if parsed_result is not None:
            break
    except Exception:
        pass
    # Invalid response or exception — retry

if parsed_result is None:
    # All attempts exhausted or no valid response
    print("value=fallback|pct=0|keep=false|confidence=0|fallback=true")
    sys.exit(0)

try:
    answers = parsed_result["answers"]

    # Detect template mode from which questions are present in the response.
    # Code mode: developer_value (choice) + necessity (choice) + primary_value_category (choice)
    # Plan mode: requirement_coverage (choice) + design_necessity (choice) + architectural_value (score)

    if "developer_value" in answers:
        # --- Code mode ---
        dev_value = answers["developer_value"]
        probs = dev_value.get("probabilities", {})
        # Decision pct: combined probability of closes_gap + improves_quality
        decision_pct = round((probs.get("closes_gap", 0) + probs.get("improves_quality", 0)) * 100)
        keep = (probs.get("closes_gap", 0) + probs.get("improves_quality", 0)) >= min_prob
        confidence = dev_value.get("confidence", 0)
        value_label = dev_value.get("choice", "unknown")

        necessity = answers.get("necessity", {})
        necessity_choice = necessity.get("choice", "unknown")

        # Primary value category for the value field
        pvc = answers.get("primary_value_category", {})
        short_label = pvc.get("choice", "unknown")

        print(f"value={short_label}|pct={decision_pct}|necessity={necessity_choice}|keep={'true' if keep else 'false'}|confidence={confidence:.2f}|fallback=false")

    elif "architectural_value" in answers:
        # --- Plan mode ---
        arch_value = answers["architectural_value"]
        probs = arch_value.get("probabilities", {})
        # Decision pct: combined probability of positive (2) + high (3)
        decision_pct = round((probs.get("2", 0) + probs.get("3", 0)) * 100)
        keep = (probs.get("2", 0) + probs.get("3", 0)) >= min_prob
        confidence = arch_value.get("confidence", 0)

        score = arch_value.get("score", 0)
        legend = arch_value.get("legend", {})
        value_label = legend.get(str(int(round(score))), "unknown")
        short_label = value_label.split(" — ")[0].split(" - ")[0].strip().replace(" ", "_")

        design_nec = answers.get("design_necessity", {})
        necessity_choice = design_nec.get("choice", "unknown")

        print(f"value={short_label}|pct={decision_pct}|necessity={necessity_choice}|keep={'true' if keep else 'false'}|confidence={confidence:.2f}|fallback=false")

    else:
        print("value=fallback|pct=0|keep=false|confidence=0|fallback=true")
        sys.exit(0)
except Exception:
    print("value=fallback|pct=0|keep=false|confidence=0|fallback=true")
    sys.exit(0)
PYEOF
