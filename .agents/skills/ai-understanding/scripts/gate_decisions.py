"""Decision-gate wiring for the ai-understanding export. This skill owns this module; the gate
itself is the copied `decisions_gate.py` (a WET copy from Mímisbrunnr), which stays a subprocess
CLI. There is no import from `mimisbrunnr-*` — the skill must run standalone.

The gate reports a per-unit verdict; this module wraps that call and turns the verdict into the
export action (`write` / `rewrite` / `held` / `mark`). It is the one place the hold/mark policy
lives, so it can be unit-tested without a live model. `--all` never changes the decision
(LADR-014): the threshold binds identically under a full-scope re-export.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
GATE = SCRIPTS / "decisions_gate.py"

ENABLED = "CONTEXT_MEMORY_DECISIONS_ENABLED"
BELOW_THRESHOLD = "CONTEXT_MEMORY_DECISIONS_BELOW_THRESHOLD"
# The credential file path is resolved per-call, never cached at import: a test or a sibling session
# may point `CONTEXT_MEMORY_CREDENTIAL_FILE` somewhere else, and a cached value would keep reading a
# real operator's file. Decision keys only — the gate never loads a token.
# Where a held unit's attempt budget must NOT live: the store is wiped by sibling sessions.
DEFAULT_LEDGER = os.path.join(
    os.environ.get("TMPDIR", os.path.expanduser("~")), "mimisbrunnr-ai-understanding-ledger.json"
)

WRITE = "write"
REWRITE = "rewrite"
HELD = "held"
MARK = "mark"

# Subprocess safety against a hung gate; the gate applies its own per-request timeout. The probe
# timeout must exceed the gate's own configured request TIMEOUT (default 30, operator-tunable via
# CONTEXT_MEMORY_DECISIONS_TIMEOUT), or a healthy-but-slow endpoint would be reported as a timeout and
# the gate silently skipped. Computed per call from the env in `gate_status`.
PROBE_TIMEOUT_BUFFER = 30
SCORE_TIMEOUT = 120


class GateRefused(RuntimeError):
    """A hard configuration refusal from the gate, not a per-unit low score.

    The copied gate exits non-zero on a bad URL, a bad `BELOW_THRESHOLD`, an unreadable rubric and the
    like — a misconfiguration it refuses rather than silently sliding every record past ungated (issue
    190). The calling export must not mistake that for "no records scored"; it must surface the refusal.
    """

    def __init__(self, outcome, detail=""):
        super().__init__(detail or outcome)
        self.outcome = outcome
        self.detail = detail or outcome


def _run_gate(args, stdin=None, env=None, timeout=30):
    return subprocess.run(
        [sys.executable, "-B", str(GATE), *args],
        input=stdin, capture_output=True, text=True, encoding="utf-8",
        env=env or os.environ.copy(), timeout=timeout,
    )


def _credential_file(env=None):
    env = env or os.environ
    return env.get("CONTEXT_MEMORY_CREDENTIAL_FILE",
                   os.path.join(os.path.expanduser("~"), ".mimisbrunnr", "credentials"))


def _flag_set_nowhere(env=None):
    """True when ENABLED appears in neither the environment nor the machine credential file."""
    env = env or os.environ
    if ENABLED in env:
        return False
    try:
        with open(_credential_file(env), encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                if line.partition("=")[0].strip() == ENABLED:
                    return False
    except OSError:
        pass
    return True


def gate_status(env=None, timeout=None):
    """Run the gate `probe`. Returns (available, reason, report_line).

    available True -> enabled and the endpoint answers. Otherwise the gate is skipped for the
    export and report_line is the one-line disclosure the export prints once (`decisions: not
    configured` / `decisions: disabled` / a classified cause). `probe` makes no network call when
    the flag is off, so an absent or disabled gate is free.
    """
    env = env or os.environ
    # The gate's own request TIMEOUT (default 30) plus a buffer, so a healthy-but-slow endpoint is
    # not reported as a probe timeout (which would skip the gate entirely).
    if timeout is None:
        try:
            configured = int(env.get("CONTEXT_MEMORY_DECISIONS_TIMEOUT", "30"))
        except ValueError:
            configured = 30
        timeout = configured + PROBE_TIMEOUT_BUFFER
    try:
        proc = _run_gate(["probe"], env=env, timeout=timeout)
    except subprocess.TimeoutExpired:
        return False, "timeout", "decisions: skipped (probe timed out)"
    except FileNotFoundError:
        return False, "gate-missing", "decisions: not configured"

    if proc.returncode != 0:
        try:
            info = json.loads(proc.stderr)
            cause = str(info.get("outcome", "error"))
        except ValueError:
            cause = "error"
        return False, cause, f"decisions: skipped ({cause})"

    try:
        report = json.loads(proc.stdout)
    except ValueError:
        return False, "bad-response", "decisions: skipped (unreadable probe response)"

    outcome = report.get("outcome")
    if outcome == "ok":
        return True, "ok", ""
    if outcome == "disabled":
        if _flag_set_nowhere(env=env):
            return False, "not-configured", "decisions: not configured"
        return False, "disabled", "decisions: disabled"
    return False, str(outcome), f"decisions: skipped ({outcome})"


def unit_record(unit):
    """A unit as the gate record. `record_state` reads subject/statement/summary/boundaries/kind/
    scope; a unit's description -> subject, answer -> statement, why -> summary, scope -> scope.
    The rest of the unit (question, confidence, slug) is not judged and is not sent."""
    return {
        "subject": unit.get("description") or unit.get("name") or "",
        "statement": unit.get("answer") or unit.get("statement") or "",
        "summary": unit.get("why") or unit.get("summary") or "",
        "boundaries": unit.get("boundaries") or "",
        "kind": "",
        "scope": unit.get("scope") or "",
    }


def score(records, ledger=None, env=None, timeout=SCORE_TIMEOUT):
    """Run the gate `score` with records (a JSON array) on stdin, bound to `ledger`.

    Returns the parsed payload. Redaction and the size guard happen inside the gate; a record the
    gate cannot inspect (redactor down, oversize) never produces a score. `ledger` is the file that
    bounds the attempt budget and must live outside the store — it defaults to `DEFAULT_LEDGER` so
    the budget is always enforced (a below-bar unit must reach `held`/`mark` after `MAX_ATTEMPTS`,
    not loop forever returning `rewrite`).

    A hard configuration refusal (bad URL, bad `BELOW_THRESHOLD`, unreadable rubric) makes the gate
    exit non-zero; this raises `GateRefused` rather than collapsing to an empty batch, so the export
    cannot silently write every unit ungated (issue 190).
    """
    if ledger is None:
        ledger = DEFAULT_LEDGER
    args = ["score"]
    if ledger is not None:
        args += ["--state-file", str(ledger)]
    try:
        proc = _run_gate(args, stdin=json.dumps(records), env=env, timeout=timeout)
    except subprocess.TimeoutExpired:
        raise GateRefused("timed-out", "gate score timed out")
    except FileNotFoundError:
        raise GateRefused("gate-missing", "decisions gate script is missing")
    if proc.returncode != 0:
        # The gate writes {"outcome": ..., "detail": ...} to stderr on a refusal.
        try:
            info = json.loads(proc.stderr)
        except ValueError:
            info = {}
        raise GateRefused(info.get("outcome", "error"),
                          info.get("detail", proc.stderr.strip() or "gate refused"))
    try:
        return json.loads(proc.stdout)
    except ValueError:
        return {"outcome": "error", "detail": proc.stderr.strip() or "gate returned no JSON",
                "records": []}


def classify(result, below_threshold="hold", max_attempts=3):
    """Map one per-record `score` result to (action, detail).

    action: WRITE | REWRITE | HELD | MARK
    - passed -> WRITE, audience = passingRoles
    - oversize -> HELD (never truncated)
    - a gate error (model-down, timeout, bad-input, redactor-unavailable...) -> WRITE ungated, with
      `detail` naming the cause — an error is never a low score.
    - not passed, attempt < max -> REWRITE
    - not passed, budget spent -> HELD or MARK per below_threshold.
    `--all` never changes this (LADR-014): the threshold binds identically under a full re-export.
    """
    outcome = result.get("outcome")
    if outcome == "oversize":
        return HELD, result.get("detail") or "oversize unit, never truncated"
    if outcome != "scored":
        if outcome == "attempts-exhausted":
            return (MARK, None) if below_threshold == "mark" else (HELD, None)
        return WRITE, f"gate skipped ({outcome})"
    if result.get("passed"):
        return WRITE, None
    if result.get("attempt", 0) < max_attempts:
        return REWRITE, None
    return (MARK, None) if below_threshold == "mark" else (HELD, None)
