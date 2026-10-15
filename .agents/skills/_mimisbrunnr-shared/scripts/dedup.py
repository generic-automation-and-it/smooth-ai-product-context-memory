#!/usr/bin/env python3
"""Staged duplicate detection for the capture path. Client-side only.

Stage 1 (exact) is the existing preflight and is not touched here. This module is stages 2 and 3:

  Stage 2, lexical candidates: for each candidate the exact stage did not match, query the store with
  1-2 distinctive subject terms (stemmed AND-of-lexemes), kind-agnostic, same group plus its repo,
  bounded to N results, exact matches excluded.

  Stage 3, decision model on doubtful pairs only: when `CONTEXT_MEMORY_DECISIONS_DEDUP_ENABLED=true`,
  ask the decision model one `noul` per (candidate, existing) pair — "do these make the same claim,
  under the same conditions?" — reusing the capture skill's decision transport (endpoint guard,
  redaction-before-call, size guard, classified outcomes). The role rubric is not reused; this is one
  question, not five.

The output is always a **proposal** under the `duplicate` menu with evidence — never a merge, version
or skip. A decision model that is disabled, down, missing or unreachable never blocks capture and never
reports "no duplicate": stage-2 candidates are shown as "possible duplicate (lexical only)" with the
reason named. Budgets bound the work and disclose `unexamined (budget)` per candidate.

Python 3.9+, stdlib only.
"""

import argparse
import json
import os
import socket
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

# Reuse the capture skill's decision-model transport. Importing the module seeds the decision settings
# from the machine credential file (its own import-time side effect), and it does not import the
# redactor at module scope, so it is safe here — the redactor runs over stdin as a subprocess, never as
# a startup dependency.
import decisions_gate as gate

SCRIPTS = Path(__file__).resolve().parent
REDACTOR = SCRIPTS / "redact.py"
READ_CLIENT = SCRIPTS / "context_memory_read_client.py"

ENV_DEDUP_ENABLED = "CONTEXT_MEMORY_DECISIONS_DEDUP_ENABLED"
ENV_DEDUP_MIN_PROBABILITY = "CONTEXT_MEMORY_DECISIONS_DEDUP_MIN_PROBABILITY"
ENV_DEDUP_MAX_PAIRS_PER_CANDIDATE = "CONTEXT_MEMORY_DECISIONS_DEDUP_MAX_PAIRS_PER_CANDIDATE"
ENV_DEDUP_MAX_PAIRS_PER_BATCH = "CONTEXT_MEMORY_DECISIONS_DEDUP_MAX_PAIRS_PER_BATCH"
ENV_DEDUP_MAX_RESULTS = "CONTEXT_MEMORY_DECISIONS_DEDUP_MAX_RESULTS"

DEFAULT_DEDUP_ENABLED = False
DEFAULT_MIN_PROBABILITY = 0.5
DEFAULT_MAX_PAIRS_PER_CANDIDATE = 3
DEFAULT_MAX_PAIRS_PER_BATCH = 20
DEFAULT_MAX_RESULTS = 5

# Same limit as the gate, and the same over-counting rationale: the guard decides whether a pair is
# safe to send, and the expensive error is refusing one that would have fit.
CHARS_PER_TOKEN = 3
MAX_BODY_BYTES = 64 * 1024

# The one `noul` question stage 3 asks. Distinct from the role rubric — this is a same-claim judgement,
# not a value judgement, and reusing the rubric's per-role criteria would score "does this help a
# developer?" instead of "is this the same claim?".
DEDUP_QUESTION = {
    "type": "noul",
    "instructions": "Do these two statements make the same claim, under the same conditions?",
    "criteria": {
        "true": "Both statements assert the same fact under the same conditions, so one is a duplicate of the other.",
        "false": "The statements differ in the claim, the conditions, or the scope — they are not duplicates.",
    },
}
DEDUP_QUESTION_KEY = "same-claim"


class DedupError(RuntimeError):
    """A refusal that names its own cause. Never carries candidate content."""

    def __init__(self, outcome, detail=""):
        super().__init__(detail or outcome)
        self.outcome = outcome
        self.detail = detail


def dedup_enabled():
    """The dedup-model switch. Anything but exactly `true` leaves stage 3 skipped."""
    return os.environ.get(ENV_DEDUP_ENABLED, "").lower() == "true"


def _number(name, fallback, cast):
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return fallback
    try:
        return cast(raw.strip())
    except ValueError:
        raise DedupError("bad-dedup-config",
                         f"{name} must be a number; refusing to run with a different value") from None


def config():
    """Every dedup setting resolved from the environment, and the decision endpoint settings reused
    from `decisions_gate`'s env names and defaults. A malformed numeric setting is refused, never
    defaulted.

    The endpoint settings are read directly from the same env names the gate reads them from, not by
    calling `gate.config()` — that function validates the gate's role rubric and ledger cap, which a
    dedup run does not use, and a malformed role list must not stop a same-claim judgement.
    """
    min_probability = _number(ENV_DEDUP_MIN_PROBABILITY, DEFAULT_MIN_PROBABILITY, float)
    if not 0.0 <= min_probability <= 1.0:
        raise DedupError("bad-dedup-config",
                         f"{ENV_DEDUP_MIN_PROBABILITY} must be between 0 and 1")
    max_pairs_per_candidate = _number(ENV_DEDUP_MAX_PAIRS_PER_CANDIDATE,
                                      DEFAULT_MAX_PAIRS_PER_CANDIDATE, int)
    if max_pairs_per_candidate < 1:
        raise DedupError("bad-dedup-config",
                         f"{ENV_DEDUP_MAX_PAIRS_PER_CANDIDATE} must be at least 1")
    max_pairs_per_batch = _number(ENV_DEDUP_MAX_PAIRS_PER_BATCH, DEFAULT_MAX_PAIRS_PER_BATCH, int)
    if max_pairs_per_batch < 1:
        raise DedupError("bad-dedup-config", f"{ENV_DEDUP_MAX_PAIRS_PER_BATCH} must be at least 1")
    max_results = _number(ENV_DEDUP_MAX_RESULTS, DEFAULT_MAX_RESULTS, int)
    if max_results < 1:
        raise DedupError("bad-dedup-config", f"{ENV_DEDUP_MAX_RESULTS} must be at least 1")

    return {
        "enabled": dedup_enabled(),
        "min_probability": min_probability,
        "max_pairs_per_candidate": max_pairs_per_candidate,
        "max_pairs_per_batch": max_pairs_per_batch,
        "max_results": max_results,
        "base_url": os.environ.get(gate.ENV_BASE_URL, gate.DEFAULT_BASE_URL).strip().rstrip("/"),
        "path": os.environ.get(gate.ENV_PATH, gate.DEFAULT_PATH).strip() or gate.DEFAULT_PATH,
        "model": os.environ.get(gate.ENV_MODEL, gate.DEFAULT_MODEL).strip() or gate.DEFAULT_MODEL,
        "api_key": os.environ.get(gate.ENV_API_KEY, "").strip(),
        "timeout": _number(gate.ENV_TIMEOUT, gate.DEFAULT_TIMEOUT, int),
    }


def redact_pair(candidate, existing):
    """Scrub both sides of a pair before any model call, or refuse. Same fail-closed contract as the gate."""
    # Reuse the gate's redaction helper: it runs the redactor over stdin and validates the arity.
    try:
        redacted, _findings = gate.redact_records([candidate, existing])
    except gate.GateError as exc:
        raise DedupError(exc.outcome, exc.detail) from None
    return redacted[0], redacted[1]


def estimate_tokens(text):
    return (len(text) + CHARS_PER_TOKEN - 1) // CHARS_PER_TOKEN


def size_guard(state):
    """Refuse an oversize pair before the request. Never truncate to fit."""
    text = json.dumps(state, ensure_ascii=False)
    total = estimate_tokens(text)
    body = len(text.encode("utf-8"))
    if total > gate.MAX_CONTEXT_TOKENS:
        return f"pair estimates to ~{total} tokens against a {gate.MAX_CONTEXT_TOKENS}-token context"
    if body > MAX_BODY_BYTES:
        return f"pair body is {body} bytes against a {MAX_BODY_BYTES}-byte limit"
    return None


def _classify(exc, timeout):
    """A transport failure named, never a probability. Same distinction as the gate."""
    if isinstance(exc, (TimeoutError, socket.timeout)):
        return DedupError("timed-out", f"no answer within {timeout}s")
    return DedupError("unreachable", "nothing accepted a connection")


def model_pair(candidate, existing, settings):
    """Ask the decision model whether one (candidate, existing) pair is the same claim.

    Returns `{"reusable": True, "probability": float, "evidence": {...}}`. Raises `DedupError` for a
    classified transport failure (disabled, unreachable, timed-out, http-*, model-missing,
    bad-response, oversize, redaction). A failure never reads as "not a duplicate".
    """
    if not settings["enabled"]:
        raise DedupError("disabled", f"{ENV_DEDUP_ENABLED} is not 'true'; stage 3 was skipped")

    candidate_state, existing_state = redact_pair(candidate, existing)

    state = {"candidate": candidate_state, "existing": existing_state}
    oversize = size_guard(state)
    if oversize:
        raise DedupError("oversize", oversize)

    payload = {
        "model": settings["model"],
        "state": state,
        "questions": {DEDUP_QUESTION_KEY: DEDUP_QUESTION},
    }
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    url = gate.resolve_endpoint(settings["base_url"], settings["api_key"]) + settings["path"]

    headers = {"Content-Type": "application/json", "Accept": "application/json"}
    if settings["api_key"]:
        headers["Authorization"] = f"Bearer {settings['api_key']}"

    request = urllib.request.Request(url, data=body, headers=headers, method="POST")
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(request, timeout=settings["timeout"]) as response:
            raw = response.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        detail = (exc.read().decode("utf-8", errors="replace") or "").strip()
        if exc.code == 404 or "model" in detail.lower():
            raise DedupError("model-missing", f"the decision model is not available ({exc.code})") from None
        raise DedupError(f"http-{exc.code}", detail or "the decision endpoint returned no detail") from None
    except urllib.error.URLError as exc:
        raise _classify(exc.reason, settings["timeout"]) from None
    except OSError as exc:
        raise _classify(exc, settings["timeout"]) from None

    try:
        document = json.loads(raw)
    except ValueError:
        raise DedupError("bad-response", "the decision endpoint returned unreadable JSON") from None
    answers = document.get("answers") if isinstance(document, dict) else None
    if not isinstance(answers, dict):
        raise DedupError("bad-response", "the decision response carried no `answers` object")
    answer = answers.get(DEDUP_QUESTION_KEY)
    value = answer.get("noul") if isinstance(answer, dict) else None
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        raise DedupError("bad-response", f"the {DEDUP_QUESTION_KEY!r} response carried no numeric `noul`")
    probability = max(0.0, min(1.0, float(value)))
    return {
        "reusable": probability > settings["min_probability"],
        "probability": probability,
        "evidence": {
            "question": DEDUP_QUESTION_KEY,
            "matched": existing.get("uuid") or existing.get("subject"),
        },
    }


def _distinctive_terms(candidate):
    """1-2 distinctive subject terms to query with. Stemmed AND-of-lexemes wants one or two key words."""
    subject = candidate.get("subject") or candidate.get("description") or candidate.get("name") or ""
    words = [w for w in subject.replace(",", " ").split() if len(w) > 3]
    words = [w for w in words if not w.lower() in ("that", "this", "with", "from", "about", "under",
                                                   "over", "into", "through", "would", "should")]
    return words[:2] or ([subject] if subject else [])


def lexical_candidates(candidate, group_uuid, repository, settings, query_fn=None):
    """Stage 2: kind-agnostic lexical candidates for one subject, bounded to N results.

    `query_fn` is an injectable seam so the harness can feed a fake store without a network call; it
    takes `(terms, group_uuid, repository, limit)` and returns a list of `{uuid, subject, statement}`.
    """
    terms = _distinctive_terms(candidate)
    if not terms:
        return []
    if query_fn is None:
        query_fn = _store_lexical_query
    try:
        results = query_fn(terms, group_uuid, repository, settings["max_results"])
    except Exception:
        # A store query failure never blocks capture: it discloses as unexamined rather than pretending
        # there is no duplicate.
        return None
    if not isinstance(results, list):
        return None
    return results


def _store_lexical_query(terms, group_uuid, repository, limit):
    """Query the read client. The client refuses to start with the write token present, so it is the
    framed read surface.

    The `/query` body uses the fields the read API declares: `query` (the AND-of-lexemes string),
    `repo`, `currentOnly`, `limit`. `kind` is omitted so the search is kind-agnostic — a cross-kind
    twin is exactly what stage 2 is for. `groupUuid` is not a `/query` field here, so it is not sent;
    the repo and current filters stand in for the "same group plus its repo" scope the store can express.
    """
    payload = {
        "query": " ".join(terms),
        "repo": repository,
        "currentOnly": True,
        "limit": limit,
    }
    proc = subprocess.run(
        [sys.executable, "-B", str(READ_CLIENT), "query", "--payload"],
        input=json.dumps(payload), capture_output=True, text=True, encoding="utf-8",
    )
    if proc.returncode != 0:
        return None
    try:
        out = json.loads(proc.stdout)
    except ValueError:
        return None
    return out.get("records") or out.get("memories") or []


def stage(candidates, group_uuid=None, repository=None, query_fn=None, model_fn=None, settings=None):
    """Run stages 2 and 3 over a batch of candidates, returning one proposal per candidate.

    `query_fn` and `model_fn` are injectable seams for the harness; `model_fn` mirrors `model_pair`
    (raises `DedupError` on a transport failure, returns a dict on success). The exact stage (stage 1)
    lives in the export's preflight and is not run here — an already-matched candidate is passed in and
    reported as `exact`, unchanged.
    """
    settings = settings or config()
    if model_fn is None:
        model_fn = model_pair

    budget_used = 0
    proposals = []
    for index, candidate in enumerate(candidates):
        if candidate.get("_exactMatch"):
            proposals.append({"index": index, "outcome": "exact", "matches": []})
            continue

        results = lexical_candidates(candidate, group_uuid, repository, settings, query_fn)
        if results is None:
            proposals.append({"index": index, "outcome": "unexamined", "detail": "lexical store query failed",
                              "matches": []})
            continue
        if not results:
            proposals.append({"index": index, "outcome": "none", "matches": []})
            continue

        remaining = settings["max_pairs_per_batch"] - budget_used
        if remaining <= 0:
            proposals.append({"index": index, "outcome": "unexamined (budget)", "matches": results})
            continue

        # Stage 3, only on the doubtful pairs, only up to the per-candidate budget.
        scored = []
        for existing in results[:settings["max_pairs_per_candidate"]]:
            if remaining <= 0:
                break
            if not settings["enabled"]:
                break
            remaining -= 1
            budget_used += 1
            try:
                verdict = model_fn(candidate, existing, settings)
            except DedupError as exc:
                # A named failure on a pair discloses that pair's reason; the candidate is still shown
                # as a lexical candidate, never reported as "no duplicate".
                scored.append({"existing": existing, "outcome": exc.outcome, "detail": exc.detail})
                continue
            scored.append({"existing": existing, **verdict})

        if not settings["enabled"]:
            proposals.append({"index": index, "outcome": "possible-duplicate",
                              "detail": "lexical only (model disabled)", "matches": results})
            continue

        proposed = [s for s in scored if s.get("reusable")]
        transport_failed = [s for s in scored
                            if s.get("outcome") in ("unreachable", "timed-out", "http-500", "disabled",
                                                    "model-missing", "bad-response", "oversize")]
        if proposed:
            proposals.append({
                "index": index, "outcome": "proposed", "detail": f"{len(proposed)} pair(s) match",
                "matches": proposed,
            })
        elif transport_failed:
            # A pair the model never judged must not let the candidate read as "no duplicate"; disclose
            # the transport reason and show the lexical candidates, exactly as a fully-disabled model.
            proposals.append({"index": index, "outcome": "possible-duplicate",
                              "detail": f"model {transport_failed[0]['outcome']}", "matches": results})
        elif scored:
            proposals.append({"index": index, "outcome": "none",
                              "detail": f"{len(scored)} pair(s) judged not duplicate", "matches": scored})
        else:
            proposals.append({"index": index, "outcome": "unexamined (budget)", "matches": results})

    return proposals


def cmd_stage(args):
    """CLI: read `{candidates, ...}` from stdin, print proposals."""
    try:
        payload = json.load(sys.stdin)
    except ValueError:
        raise DedupError("bad-input", "stdin did not carry a JSON object") from None
    if not isinstance(payload, dict) or not isinstance(payload.get("candidates"), list):
        raise DedupError("bad-input", "stdin must carry {'candidates': [...]}")

    settings = config()
    group_uuid = payload.get("groupUuid")
    repository = payload.get("repository")
    proposals = stage(payload["candidates"], group_uuid, repository, settings=settings)
    print(json.dumps({"outcome": "ok", "proposals": proposals}, indent=2))
    return 0


def main():
    parser = argparse.ArgumentParser(prog="dedup", description="Staged duplicate detection for the capture path.")
    sub = parser.add_subparsers(dest="command", required=True)
    stage_parser = sub.add_parser("stage", help="run stages 2 and 3 over candidates from stdin")
    stage_parser.set_defaults(func=cmd_stage)

    args = parser.parse_args()
    try:
        return args.func(args)
    except DedupError as exc:
        print(json.dumps({"outcome": exc.outcome, "detail": exc.detail}, indent=2), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
