# AI Decisions — Ralph Loop Validation Layer

Opt-in decision validation for ralph loop findings. Evaluates each fix via a local OpenJEV model to determine if the change provides value, is correct, and what risk it carries.

## Prerequisites

- OpenJEV running at `SMOOTH_DEVEX_DECISIONS_BASE_URL` (default: `http://127.0.0.1:1234`)
- `python3` available (already a ralph loop dependency)

## Enable

Set via the skills installer interactive prompt, or manually:

```bash
# Add to ~/.zshrc (macOS) or ~/.bashrc (Linux):
export SMOOTH_DEVEX_DECISIONS_ENABLED=true
export SMOOTH_DEVEX_DECISIONS_BASE_URL=http://127.0.0.1:1234
export SMOOTH_DEVEX_DECISIONS_PATH=/v1/systemone
export SMOOTH_DEVEX_DECISIONS_MODEL=openjev
export SMOOTH_DEVEX_DECISIONS_MIN_PROBABILITY=0.60
export SMOOTH_DEVEX_DECISIONS_MAX_ATTEMPTS=3
export SMOOTH_DEVEX_DECISIONS_TIMEOUT=60
```

The installer also sets `decisions_enabled: true` in `_smooth-devex-shared/installation.json`.

## Environment Variables

| Variable | Default | Purpose |
|----------|---------|---------|
| `SMOOTH_DEVEX_DECISIONS_ENABLED` | _(unset)_ | Must be `true` to activate decision validation |
| `SMOOTH_DEVEX_DECISIONS_BASE_URL` | `http://127.0.0.1:1234` | OpenJEV service base URL |
| `SMOOTH_DEVEX_DECISIONS_PATH` | `/v1/systemone` | API endpoint path |
| `SMOOTH_DEVEX_DECISIONS_MODEL` | `openjev` | Model name to request |
| `SMOOTH_DEVEX_DECISIONS_MIN_PROBABILITY` | `0.60` | Minimum probability (0-1) for the fix to be marked `keep=true` |
| `SMOOTH_DEVEX_DECISIONS_MAX_ATTEMPTS` | `3` | Retry count on timeout or connection failure |
| `SMOOTH_DEVEX_DECISIONS_TIMEOUT` | `60` | Per-request timeout in seconds |
| `SMOOTH_DEVEX_DECISIONS_TEMPLATE` | `templates/build.json` | Override template path (relative to script) |

## How It Works

1. Ralph loop triage accepts a finding and produces a fix (before/after code).
2. `decision-evaluate.sh` reads the question template, injects before/after code into the state, and calls OpenJEV.
3. Two templates are available:
   - **Code mode** (`templates/build.json`) — asks:
     - **developer_value** — does this change minimize a feature gap? (closes_gap / improves_quality / marginal / no_value)
     - **necessity** — is it necessary for the PR? (essential / important / optional / unnecessary)
     - **primary_value_category** — what kind of value? (correctness / security / test_coverage / maintainability)
   - **Plan mode** (`templates/plan.json`) — asks:
     - **requirement_coverage** — does a cited requirement ask for this? (directly_required / implicitly_needed / nice_to_have / not_required)
     - **design_necessity** — is it necessary for the system? (essential / important / beneficial / unnecessary)
     - **architectural_value** — overall architectural worth? (harmful → high)
4. Decision percentage:
   - Code mode = P(closes_gap) + P(improves_quality)
   - Plan mode = P(positive) + P(high) on architectural_value
5. If percentage >= `MIN_PROBABILITY`, the fix is marked `keep=true`.
6. On timeout or failure (after retries), falls back to existing ralph behavior — no gate.

Use `SMOOTH_DEVEX_DECISIONS_TEMPLATE` to select the plan template when running in plan mode.

## Output Format

Pipe-delimited on stdout:

```
value=correctness|pct=72|necessity=essential|keep=true|confidence=0.35|fallback=false
```

| Field | Meaning |
|-------|---------|
| `value` | Code mode: `primary_value_category` (`correctness`, `security`, `test_coverage`, `maintainability`); Plan mode: legend short-label |
| `pct` | Decision percentage (0-100) — probability the fix has positive or high net value |
| `necessity` | Code mode: template choice (`essential`, `important`, `optional`, `unnecessary`); Plan mode: `design_necessity` |
| `keep` | Whether the probability meets the threshold |
| `confidence` | Model confidence score |
| `fallback` | `true` when the service was unreachable or returned invalid data |

## Customising Templates

Edit `templates/build.json` (code mode) or `templates/plan.json` (plan mode) to change the questions, criteria, or state prompt. The template uses these placeholders:

| Placeholder | Replaced with |
|-------------|---------------|
| `{{CATEGORY}}` | Ralph loop category name |
| `{{BEFORE}}` | Code/plan text before the fix |
| `{{AFTER}}` | Code/plan text after the fix |
| `{{CONTEXT}}` | File:line or section:paragraph location |

The `questions` object follows the OpenJEV API format — `type` is `choice` or `score`, with `instructions` and `criteria`.

The script auto-detects which template is in use by checking which question names appear in the response:
- `developer_value` present → code mode (pct = closes_gap + improves_quality)
- `architectural_value` present → plan mode (pct = positive + high scores)

## Disable

Remove `SMOOTH_DEVEX_DECISIONS_ENABLED=true` from your shell profile, or set it to `false`. The ralph loop runs without decision validation. Set `decisions_enabled: false` in `installation.json` to prevent the installer from re-enabling it.
