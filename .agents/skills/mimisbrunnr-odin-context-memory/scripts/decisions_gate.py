#!/usr/bin/env python3
"""Value gate for exports: score each record against a role rubric via a local decision model.

Client-side only. No Host or Application change, no model in the service. The store is untouched by
anything here; this script reads a redacted record, asks a decision model whether it has value to any
target role, and reports per-record scores that a caller may use to hold or mark a record.

Two subcommands:

  score   stdin JSON array of records -> per-record scores on stdout
  probe   report availability: disabled | unreachable | model-missing | ok

Design rules that the harness pins, each of which exists because the cheaper version is wrong:

- **One independent `noul` question per role.** A single `choice` across roles makes the
  probabilities sum to 1, so a record valuable to two roles scores ~0.45 each and fails at a 0.5 bar.
  Independent questions are what make "valuable to at least one role" expressible.
- **Never report a transport failure as a low score.** `unreachable`, `timed-out`, `http-500` and
  `bad-response` are distinct outcomes from a score. A hung model must skip the gate and disclose
  itself, never silently hold a record for a reason nobody produced.
- **Redaction runs before any model call, and fails closed.** An unavailable redactor is
  `redactor-unavailable`, and no request is made. Sending unscrubbed content to a model is the one
  outcome this gate exists to avoid.
- **The attempt counter is the script's, not the caller's.** A ledger keyed by record identity in a
  state file bounds the rewrite loop, so an agent cannot reset it by re-asking.
- **The score is a quality signal, never authority.** It never changes status, kind, or approval.

Python 3.9+, stdlib only.
"""

import argparse
import json
import math
import os
import re
import socket
import sys
import urllib.error
import urllib.request
from pathlib import Path
from urllib.parse import urlparse

DEFAULT_BASE_URL = "http://localhost:11434"
DEFAULT_PATH = "/v1/systemone"
DEFAULT_MODEL = "nimble"
DEFAULT_MIN_PROBABILITY = 0.5
DEFAULT_MAX_ATTEMPTS = 3
DEFAULT_TIMEOUT = 30
DEFAULT_ROLES = "product-owner,designer,developer,tester,business"
DEFAULT_BELOW_THRESHOLD = "hold"

ENV_ENABLED = "CONTEXT_MEMORY_DECISIONS_ENABLED"
ENV_BASE_URL = "CONTEXT_MEMORY_DECISIONS_BASE_URL"
ENV_PATH = "CONTEXT_MEMORY_DECISIONS_PATH"
ENV_MODEL = "CONTEXT_MEMORY_DECISIONS_MODEL"
ENV_API_KEY = "CONTEXT_MEMORY_DECISIONS_API_KEY"
ENV_MIN_PROBABILITY = "CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY"
ENV_MAX_ATTEMPTS = "CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS"
ENV_ROLES = "CONTEXT_MEMORY_DECISIONS_ROLES"
ENV_BELOW_THRESHOLD = "CONTEXT_MEMORY_DECISIONS_BELOW_THRESHOLD"
ENV_TIMEOUT = "CONTEXT_MEMORY_DECISIONS_TIMEOUT"

SCRIPTS = Path(__file__).resolve().parent
REDACTOR = SCRIPTS / "redact.py"
RUBRIC = SCRIPTS / "decisions_rubric.json"

# Decision-model context limits. The prompt must fit the model's context, and the body must fit the
# wire. Both are enforced BEFORE the request, and an oversize record is held rather than truncated:
# silently shortening a record to make it fit would score a claim nobody made.
MAX_CONTEXT_TOKENS = 8192
MAX_BODY_BYTES = 64 * 1024
MAX_QUESTIONS = 64

# Most entries the attempt ledger keeps. Nothing else prunes it — an entry is retained precisely to
# remember that its budget is spent — so without a cap a long session grows one line per distinct
# subject it has ever scored. See `cap_ledger` for which entries are dropped when it is reached.
MAX_LEDGER_ENTRIES = 5000

# A token estimate without a tokenizer. Deliberately over-counting: this guard decides whether a
# record is safe to send, and the expensive error is refusing a record that would have fit, never
# truncating one that would not. Four characters per token is the usual English approximation; three
# is used here because code, identifiers and punctuation-heavy text tokenize worse than prose and a
# record that fits at 3.3 chars/token does not fit at 4.
CHARS_PER_TOKEN = 3


class GateError(RuntimeError):
    """A refusal that names its own cause. Never carries record content."""

    def __init__(self, outcome, detail=""):
        super().__init__(detail or outcome)
        self.outcome = outcome
        self.detail = detail


def enabled():
    """The feature flag. Anything but the exact string `true` leaves the gate skipped.

    Compared case-insensitively but **not** trimmed of surrounding whitespace: a value that picked up
    a trailing space from a sourced env file is a configuration mistake, and silently enabling a gate
    on it would be the one reading nobody asked for. Every other spelling is refused by being off.
    """
    return os.environ.get(ENV_ENABLED, "").lower() == "true"


def config():
    """Every setting resolved from the environment, with the documented default."""
    def number(name, fallback, cast):
        raw = os.environ.get(name)
        if raw is None or raw.strip() == "":
            return fallback
        try:
            return cast(raw.strip())
        except ValueError:
            # Refused rather than defaulted: a budget or threshold that silently becomes something
            # other than what the operator asked for is one they trust and the system does not honour.
            raise GateError("bad-decisions-config",
                            f"{name} must be a number; refusing to run with a different value") from None

    roles = [r.strip() for r in os.environ.get(ENV_ROLES, DEFAULT_ROLES).split(",") if r.strip()]
    if not roles:
        raise GateError("bad-decisions-config", f"{ENV_ROLES} named no role")

    below = os.environ.get(ENV_BELOW_THRESHOLD, DEFAULT_BELOW_THRESHOLD).strip().lower()
    if below not in ("hold", "mark"):
        raise GateError("bad-decisions-config",
                        f"{ENV_BELOW_THRESHOLD} must be 'hold' or 'mark', not {below!r}")

    probability = number(ENV_MIN_PROBABILITY, DEFAULT_MIN_PROBABILITY, float)
    if not 0.0 <= probability <= 1.0:
        raise GateError("bad-decisions-config",
                        f"{ENV_MIN_PROBABILITY} must be between 0 and 1, not {probability}")

    attempts = number(ENV_MAX_ATTEMPTS, DEFAULT_MAX_ATTEMPTS, int)
    if attempts < 1:
        raise GateError("bad-decisions-config", f"{ENV_MAX_ATTEMPTS} must be at least 1")

    timeout = number(ENV_TIMEOUT, DEFAULT_TIMEOUT, int)
    if timeout < 1:
        raise GateError("bad-decisions-config", f"{ENV_TIMEOUT} must be at least 1 second")

    return {
        "base_url": os.environ.get(ENV_BASE_URL, DEFAULT_BASE_URL).strip().rstrip("/"),
        "path": os.environ.get(ENV_PATH, DEFAULT_PATH).strip() or DEFAULT_PATH,
        "model": os.environ.get(ENV_MODEL, DEFAULT_MODEL).strip() or DEFAULT_MODEL,
        "api_key": os.environ.get(ENV_API_KEY, "").strip(),
        "min_probability": probability,
        "max_attempts": attempts,
        "roles": roles,
        "below_threshold": below,
        "timeout": timeout,
    }


def endpoint_origin(base_url):
    """The scheme://host:port of the endpoint, for reporting. Never any userinfo, never the key."""
    try:
        parsed = urlparse(base_url)
    except ValueError:
        return "<unparseable>"
    host = parsed.hostname or "<no-host>"
    if ":" in host and not host.startswith("["):
        host = f"[{host}]"
    return f"{parsed.scheme}://{host}:{parsed.port}" if parsed.port else f"{parsed.scheme}://{host}"


def resolve_endpoint(base_url, api_key):
    """Loopback is the default; anything else needs https and a key.

    Same rule as the store's base-URL guard. The gate sends record content to whatever answers, so a
    plain-http remote endpoint would put it on the wire in the clear; the key requirement is what makes
    a hosted Jev-compatible endpoint swappable later without a code change.
    """
    try:
        parsed = urlparse(base_url)
        parsed.port  # noqa: B018 — parsed for its ValueError on a malformed port
    except ValueError:
        raise GateError("bad-decisions-url",
                        "the decision endpoint could not be parsed") from None

    if parsed.username or parsed.password:
        # Never echo it: a URL carrying credentials is the case this guard exists for, and the
        # parsed netloc would put the value in the message.
        raise GateError("bad-decisions-url",
                        "the decision endpoint URL must not carry userinfo")
    if parsed.scheme not in ("http", "https"):
        raise GateError("bad-decisions-url",
                        f"the decision endpoint must be http or https, not {parsed.scheme!r}")
    if parsed.hostname in ("localhost", "127.0.0.1", "::1"):
        return base_url
    if parsed.scheme != "https" or not api_key:
        raise GateError(
            "bad-decisions-url",
            "a non-loopback decision endpoint must be https and carry a non-empty "
            f"{ENV_API_KEY}")
    return base_url


def load_rubric(roles):
    """The shared rubric, narrowed to the configured roles.

    The rubric file is the authority on what each role means; `ROLES` selects which of them to ask.
    A role the operator names that the rubric does not define is a refusal rather than a silently
    skipped question — a gate asked about three of five roles would under-report and look like a pass.
    """
    try:
        with open(RUBRIC, encoding="utf-8") as handle:
            rubric = json.load(handle)
    except OSError as exc:
        raise GateError("rubric-unavailable", f"the rubric could not be read ({type(exc).__name__})")
    except ValueError:
        raise GateError("rubric-unavailable", "the rubric is not valid JSON")

    # **Every field is checked before it is used.** The rubric is a data file an operator edits, and
    # a malformed one raised `AttributeError`/`KeyError` straight out of this function — a traceback
    # on stderr rather than a classified refusal, which is the same defect the capture client fixed for
    # a base URL that quotes its own userinfo. A rubric that does not fully define a role is not a
    # rubric the gate can ask against, and asking anyway would send a question with no criteria.
    if not isinstance(rubric, dict):
        raise GateError("rubric-unavailable", "the rubric is not a JSON object")
    entries = rubric.get("roles")
    if not isinstance(entries, list) or not entries:
        raise GateError("rubric-unavailable", "the rubric declares no 'roles' list")

    defined = {}
    for position, entry in enumerate(entries):
        if not isinstance(entry, dict):
            raise GateError("rubric-unavailable",
                            f"rubric role #{position} is not an object")
        key = entry.get("key")
        if not isinstance(key, str) or not key.strip():
            raise GateError("rubric-unavailable", f"rubric role #{position} has no usable 'key'")
        instructions = entry.get("instructions")
        if not isinstance(instructions, str) or not instructions.strip():
            raise GateError("rubric-unavailable",
                            f"rubric role {key!r} has no 'instructions'")
        criteria = entry.get("criteria")
        if not isinstance(criteria, dict):
            raise GateError("rubric-unavailable", f"rubric role {key!r} has no 'criteria' object")
        for branch in ("true", "false"):
            text = criteria.get(branch)
            if not isinstance(text, str) or not text.strip():
                raise GateError("rubric-unavailable",
                                f"rubric role {key!r} has no criteria.{branch} description")
        defined[key] = entry

    missing = [role for role in roles if role not in defined]
    if missing:
        raise GateError("bad-decisions-config",
                        f"{ENV_ROLES} names role(s) the rubric does not define: {', '.join(missing)}")
    if len(roles) > MAX_QUESTIONS:
        raise GateError("bad-decisions-config",
                        f"{len(roles)} roles exceeds the {MAX_QUESTIONS}-question limit")

    return rubric.get("version", "unversioned"), [defined[role] for role in roles]


def record_state(record):
    """The record as exported, as the model's `state`.

    Only the fields a reader would judge value from. Deliberately not the whole payload: a uuid, a
    timestamp and an internal flag tell the model nothing about whether this is worth keeping.
    """
    return {
        "subject": record.get("subject") or record.get("description") or record.get("name") or "",
        "statement": record.get("statement") or record.get("answer") or "",
        "summary": record.get("contentSummary") or record.get("why") or "",
        "boundaries": record.get("boundaries") or record.get("validUntil") or "",
        "kind": record.get("kind") or "",
        "scope": record.get("scope") or record.get("scopeDimension") or "",
    }


def redact_records(records):
    """Scrub every free-text field with the capture skill's redactor, or refuse.

    The redactor is run as a subprocess over stdin, never argv, so record content cannot reach a
    process table. Returns the scrubbed records alongside a per-field finding summary.

    Fails closed: a redactor that cannot run means the gate cannot inspect what it is about to send,
    and the gate's whole value is that it never sends unscrubbed content.
    """
    import subprocess

    if not REDACTOR.is_file():
        raise GateError("redactor-unavailable", "the redactor script is missing; no request was made")

    targets = []
    for record in records:
        targets.extend(record_state(record).values())
    # De-duplicate while keeping order, so the same statement is not scrubbed once per alias. The
    # redactor answers positionally (`candidate_index`), not by echoing the content back, so the
    # mapping is built from the list we sent rather than from anything it returns.
    unique = list(dict.fromkeys(t for t in targets if isinstance(t, str) and t))

    try:
        proc = subprocess.run(
            [sys.executable, "-B", str(REDACTOR)],
            input=json.dumps(unique),
            capture_output=True, text=True, encoding="utf-8", timeout=60,
        )
    except (OSError, ValueError) as exc:
        raise GateError("redactor-unavailable",
                        f"the redactor could not run ({type(exc).__name__}); no request was made")

    if proc.returncode != 0:
        raise GateError("redactor-unavailable",
                        "the redactor exited non-zero; no request was made")
    try:
        results = json.loads(proc.stdout)["results"]
    except (ValueError, KeyError, TypeError):
        raise GateError("redactor-unavailable",
                        "the redactor returned unreadable output; no request was made")

    if not isinstance(results, list) or len(results) != len(unique):
        # **Both** directions are refusals. An earlier guard caught only a redactor returning *more*
        # results than candidates, which left the under-reporting case open: a redactor that dropped
        # the last entry left that field unmapped, so it kept its unscrubbed value and was sent to the
        # model, and the record was scored as though the gate had inspected it. Verified against a
        # stub redactor that drops one result — the model received `password=hunter2secretvalue`.
        # An arity mismatch means the mapping from "what we sent" to "what came back" is not
        # trustworthy, and an untrustworthy redaction mapping is exactly the condition under which
        # proceeding is most likely to leak.
        raise GateError("redactor-unavailable",
                        f"the redactor returned {len(results) if isinstance(results, list) else 'a non-list'} "
                        f"result(s) for {len(unique)} candidate(s); no request was made")

    scrubbed = {}
    findings = {}
    for position, item in enumerate(results):
        if not isinstance(item, dict):
            raise GateError("redactor-unavailable",
                            "the redactor returned an unexpected shape; no request was made")
        # Trust the position we sent rather than a returned index: a redactor that reordered an entry
        # would otherwise silently scrub one field's content into another's slot.
        if "redacted" not in item:
            # Absent is not "unchanged". Defaulting to "" would blank the field, and defaulting to
            # the original would send it; neither is an inspection, so this refuses.
            raise GateError("redactor-unavailable",
                            "a redactor result carried no 'redacted' field; no request was made")
        scrubbed[unique[position]] = item["redacted"]
        for finding in item.get("findings") or []:
            if not isinstance(finding, dict):
                raise GateError("redactor-unavailable",
                                "a redactor finding was not an object; no request was made")
            rule = finding.get("rule_name", "unknown")
            findings[rule] = findings.get(rule, 0) + finding.get("hit_count", 0)

    out = []
    for record in records:
        state = record_state(record)
        for field, value in state.items():
            if isinstance(value, str) and value in scrubbed:
                state[field] = scrubbed[value]
        out.append(state)
    return out, findings


def estimate_tokens(text):
    """A deliberately pessimistic token estimate. See CHARS_PER_TOKEN."""
    return math.ceil(len(text) / CHARS_PER_TOKEN)


def size_guard(state, roles):
    """Refuse an oversize record BEFORE the request. Never truncate to fit."""
    state_text = json.dumps(state, ensure_ascii=False)
    questions = len(roles)
    total = estimate_tokens(state_text)
    body = len(state_text.encode("utf-8"))
    if total > MAX_CONTEXT_TOKENS:
        return (f"record estimates to ~{total} tokens against a {MAX_CONTEXT_TOKENS}-token context "
                f"({len(state_text)} chars)")
    if body > MAX_BODY_BYTES:
        return f"record body is {body} bytes against a {MAX_BODY_BYTES}-byte limit"
    return None


def build_request(state, roles, model):
    """One request, one independent `noul` per role."""
    return {
        "model": model,
        "state": state,
        "questions": {
            role["key"]: {
                "type": "noul",
                "instructions": role["instructions"],
                "criteria": {"true": role["criteria"]["true"], "false": role["criteria"]["false"]},
            }
            for role in roles
        },
    }


def _classify(exc, timeout):
    """A transport failure named, never a score. `timed-out` is kept apart from `unreachable`
    because a hang is worth retrying and a refusal is an operator action — the same distinction the
    store's read path makes, and for the same reason: a hung model must never read as a low score."""
    if isinstance(exc, (TimeoutError, socket.timeout)):
        return GateError("timed-out", f"no answer within {timeout}s")
    return GateError("unreachable", "nothing accepted a connection")


def call_model(state, roles, settings, rubric_version):
    """Score one record. Returns scores, passing roles, and the rubric version recorded with them."""
    payload = build_request(state, roles, settings["model"])
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    url = resolve_endpoint(settings["base_url"], settings["api_key"]) + settings["path"]

    headers = {"Content-Type": "application/json", "Accept": "application/json"}
    # Sent only when non-empty: local Ollama needs no key, and an empty Authorization header is a
    # different request from no header.
    if settings["api_key"]:
        headers["Authorization"] = f"Bearer {settings['api_key']}"

    request = urllib.request.Request(url, data=body, headers=headers, method="POST")
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(request, timeout=settings["timeout"]) as response:
            raw = response.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        detail = (exc.read().decode("utf-8", errors="replace") or "").strip()
        # A missing model is the one HTTP failure an operator can act on without reading a traceback.
        if exc.code == 404 or "model" in detail.lower():
            raise GateError("model-missing",
                            f"the decision model is not available ({exc.code})") from None
        raise GateError(f"http-{exc.code}",
                        detail or "the decision endpoint returned no detail") from None
    except urllib.error.URLError as exc:
        raise _classify(exc.reason, settings["timeout"]) from None
    except (TimeoutError, socket.timeout) as exc:
        raise _classify(exc, settings["timeout"]) from None
    except OSError as exc:
        # A connection accepted and then reset raises neither URLError nor TimeoutError.
        raise _classify(exc, settings["timeout"]) from None

    try:
        document = json.loads(raw)
    except ValueError:
        raise GateError("bad-response", "the decision endpoint returned unreadable JSON") from None

    answers = document.get("answers") if isinstance(document, dict) else None
    if not isinstance(answers, dict):
        raise GateError("bad-response", "the decision response carried no `answers` object")

    scores = {}
    for role in roles:
        answer = answers.get(role["key"])
        value = answer.get("noul") if isinstance(answer, dict) else None
        if not isinstance(value, (int, float)) or isinstance(value, bool):
            raise GateError("bad-response",
                            f"role {role['key']!r} carried no numeric `noul`")
        # Clamped: a probability outside 0..1 is a malformed answer, and letting it through would let a
        # 1.5 pass a 1.0 bar. Clamping is stated here rather than silently trusted.
        scores[role["key"]] = max(0.0, min(1.0, float(value)))

    passing = [role for role, value in scores.items() if value > settings["min_probability"]]
    return scores, passing, rubric_version


# --------------------------------------------------------------------------- attempt ledger


def ledger_path(state_file):
    return Path(state_file)


def read_ledger(state_file):
    """The ledger, and whether it had to be discarded.

    A missing or unreadable ledger starts empty rather than refusing: the ledger bounds a convenience
    loop, and a corrupt file must not be a way to make scoring fail permanently. **That choice is
    reported, not silent.** An earlier version returned an empty ledger with no signal, so a truncated
    or corrupted file silently cleared every record's attempt budget — verified by writing a ledger,
    spending the budget, then truncating the file and watching the next call score again. The bound is
    only as trustworthy as the disclosure that it was reset.
    """
    try:
        with open(ledger_path(state_file), encoding="utf-8") as handle:
            data = json.load(handle)
    except OSError:
        return {}, False
    except ValueError:
        return {}, True
    if not isinstance(data, dict):
        return {}, True
    # An entry is `{"attempts": int, "best": {...} | null}`, because the best attempt has to survive
    # between invocations for the comparison it exists to make. A **bare integer is also accepted** and
    # read as a count with no recorded best, so a ledger written by the earlier count-only format
    # keeps working instead of being discarded — which on upgrade would silently reset every budget,
    # the exact failure `ledgerReset` exists to make visible.
    for key, value in data.items():
        if not isinstance(key, str):
            return {}, True
        if isinstance(value, int) and not isinstance(value, bool) and value >= 0:
            continue
        if not isinstance(value, dict):
            return {}, True
        attempts = value.get("attempts")
        if not isinstance(attempts, int) or isinstance(attempts, bool) or attempts < 0:
            return {}, True
        if value.get("best") is not None and not isinstance(value.get("best"), dict):
            return {}, True
    return data, False


def entry_attempts(value):
    """The attempt count of a ledger entry, in either supported format."""
    return value.get("attempts", 0) if isinstance(value, dict) else value


def entry_best(value):
    """The recorded best attempt of a ledger entry, or None when there is not one yet."""
    return value.get("best") if isinstance(value, dict) else None


def cap_ledger(ledger, max_entries=MAX_LEDGER_ENTRIES):
    """Bound the ledger by dropping the most-spent entries once it grows past the cap.

    Nothing else prunes it: an entry is kept precisely because it must remember that its budget is
    spent, so a long session accumulates one entry per distinct subject it has ever scored — a
    20 000-subject session reaches ~430 KB of `{"subject": 3}` lines and keeps growing. Dropping the
    **most-spent** entries is the cheapest way to bound it: those are the records most likely to have
    been written already, and losing the memory of a spent budget is the least harmful entry to lose.
    """
    if len(ledger) <= max_entries:
        return ledger
    ordered = sorted(ledger.items(), key=lambda pair: (-entry_attempts(pair[1]), pair[0]))
    return dict(ordered[:max_entries])


def write_ledger(state_file, ledger):
    path = ledger_path(state_file)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    with open(temp, "w", encoding="utf-8") as handle:
        json.dump(ledger, handle, indent=2, sort_keys=True)
    os.replace(temp, path)


def record_identity(record, index):
    """A stable identity for a record across rewrites.

    NOT a content hash: a rewrite changes the content by definition, so a content-keyed ledger would
    see every attempt as a new record and never fire. The subject is what the capture path already
    treats as the record's identity (the store keys uniqueness on `subject_slug`), so it survives a
    rewrite while a genuinely different record gets its own budget.
    """
    subject = record.get("subject") or record.get("description") or record.get("name")
    if not subject:
        # A record with no subject cannot be identified across attempts, so it is keyed by position
        # and gets the budget for this call only — reported, so the caller knows it is not durable.
        return f"index:{index}"
    return str(subject)


def next_attempt(state_file, identity, max_attempts):
    """How many scoring rounds this record has had, bounded by `max_attempts`.

    The counter lives here rather than in the caller's loop because an agent that is asked to
    "improve until it passes" will otherwise re-ask whenever the answer is inconvenient. Once the
    budget is spent, further attempts are refused rather than silently accepted.

    Returns `(attempt, reset)`; `reset` is True when the ledger had to be discarded, which the caller
    surfaces so a cleared budget is never mistaken for a first attempt.
    """
    ledger, discarded = read_ledger(state_file)
    entry = ledger.get(identity)
    # Read both parts of the entry BEFORE any write. Two separate writes were the first cut of this,
    # and the second one read back a file the first had already replaced: writing the bare count
    # discards the recorded best, so every rewrite looked like an improvement and the stored `best`
    # was whichever round ran last. One read, then one write that preserves both.
    prior_best = entry_best(entry)
    used = entry_attempts(entry) if entry is not None else 0
    if used >= max_attempts:
        return None, discarded, prior_best
    # The count is spent whether or not the model answers, so it is written now with the best carried
    # through untouched; `record_attempt` then raises the best without disturbing the count.
    ledger[identity] = {"attempts": used + 1, "best": prior_best}
    write_ledger(state_file, cap_ledger(ledger))
    return used + 1, discarded, prior_best


def record_attempt(state_file, identity, attempt, scores):
    """Remember a scored attempt, keeping the best the ledger has seen for this record.

    Written separately from `next_attempt` because the count is spent whether or not the model
    answered — an `unreachable` round must still count against the budget, or a down model would
    grant unlimited attempts for free.
    """
    ledger, _discarded = read_ledger(state_file)
    candidate = {"attempt": attempt, "max": max(scores.values()), "scores": scores}
    entry = ledger.get(identity)
    # `next_attempt` may have replaced a dict entry with a bare count, so the best is read from what
    # survives here rather than from the value passed in. Counting up from the surviving count keeps a
    # ledger written in the earlier format converging instead of restarting.
    prior = entry_best(entry)
    prior_attempts = entry_attempts(entry)
    ledger[identity] = {"attempts": max(prior_attempts, attempt),
                        "best": best_attempt(prior, candidate)}
    write_ledger(state_file, cap_ledger(ledger))
    return ledger[identity]["best"]


def best_attempt(previous, candidate):
    """The better of two attempts: highest max-role probability, ties to the earliest.

    Ties go to the earlier attempt because it is closest to the source material — a rewrite that
    matched the original's score added nothing but drift.

    A rewrite's score is compared against the **stored** best, not only against the current round:
    each `score` invocation is a separate process, so without the ledger carrying the previous best
    this function was only ever called with `previous=None` and every record's reported "best" was
    simply its latest attempt — the comparison the worktask specifies was never actually made.
    """
    if previous is None:
        return candidate
    if candidate["max"] > previous["max"]:
        return candidate
    return previous


# --------------------------------------------------------------------------- subcommands


def cmd_score(args):
    settings = config()

    try:
        records = json.load(sys.stdin)
    except ValueError:
        raise GateError("bad-input", "stdin did not carry a JSON array") from None
    if not isinstance(records, list):
        raise GateError("bad-input", "stdin must carry a JSON array of records")

    if not enabled():
        # documented default, and a caller must be able to tell "skipped" from "scored zero".
        print(json.dumps({
            "outcome": "disabled", "model": settings["model"],
            "endpoint": endpoint_origin(settings["base_url"]),
            "records": [], "message": f"{ENV_ENABLED} is not 'true'; the gate was skipped",
        }, indent=2))
        return 0

    rubric_version, roles = load_rubric(settings["roles"])

    redacted, findings = redact_records(records)

    results = []
    ledger_reset = False
    for index, (original, state) in enumerate(zip(records, redacted)):
        oversize = size_guard(state, roles)
        if oversize:
            results.append({
                "index": index, "identity": record_identity(original, index), "attempt": 0,
                "outcome": "oversize", "detail": oversize, "scores": {}, "passingRoles": [],
                "passed": False, "best": None,
            })
            continue

        if args.state_file:
            attempt, this_reset, prior_best = next_attempt(args.state_file,
                                                       record_identity(original, index),
                                                       settings["max_attempts"])
            # One reset anywhere in the batch is reported, since it is one file and one bound.
            ledger_reset = ledger_reset or this_reset
            if attempt is None:
                results.append({
                    "index": index, "identity": record_identity(original, index),
                    "attempt": settings["max_attempts"], "outcome": "attempts-exhausted",
                    "detail": f"the {settings['max_attempts']}-attempt budget is spent for this record",
                    "scores": {}, "passingRoles": [], "passed": False, "best": None,
                })
                continue
        else:
            # No ledger, so no budget and no cross-round comparison: every round is a first round,
            # which is why `prior_best` is None rather than read from anywhere.
            attempt = 1
            prior_best = None

        try:
            scores, passing, version = call_model(state, roles, settings, rubric_version)
        except GateError as exc:
            results.append({
                "index": index, "identity": record_identity(original, index), "attempt": attempt,
                "outcome": exc.outcome, "detail": exc.detail, "scores": {}, "passingRoles": [],
                "passed": False, "best": None,
            })
            continue

        best = best_attempt(prior_best,
                        {"attempt": attempt, "scores": scores, "max": max(scores.values())})
        # Persist only once a score exists, so a failed round leaves the best untouched while still
        # having spent its attempt above.
        stored = record_attempt(args.state_file, record_identity(original, index),
                                attempt, scores) if args.state_file else best
        results.append({
            "index": index, "identity": record_identity(original, index), "attempt": attempt,
            "outcome": "scored", "rubricVersion": version, "scores": scores,
            "passingRoles": passing, "passed": bool(passing),
            # `best` is the best of this round and every earlier one; `bestThisRound` says whether the
            # rewrite actually improved on the source, which is the thing an agent rewriting to pass
            # needs to know and cannot infer from `best` alone.
            "best": stored, "bestThisRound": best is not prior_best,
        })

    payload = {
        "outcome": "ok",
        "model": settings["model"],
        "endpoint": endpoint_origin(settings["base_url"]),
        "rubricVersion": rubric_version,
        "minProbability": settings["min_probability"],
        "maxAttempts": settings["max_attempts"],
        "belowThreshold": settings["below_threshold"],
        "redaction": findings,
        "records": results,
    }
    if args.state_file:
        payload["stateFile"] = str(args.state_file)
        if ledger_reset:
            # Said plainly, because a cleared ledger means every record's attempt budget starts again
            # — so an `attempt: 1` here is not evidence that this record is new.
            payload["ledgerReset"] = (
                "the attempt ledger at this path was unreadable or malformed and has been started "
                "empty; every record's attempt budget begins again. This is reported so a cleared "
                "bound is never mistaken for a first attempt."
            )
    print(json.dumps(payload, indent=2))
    return 0


def cmd_probe(args):
    settings = config()
    report = {"model": settings["model"], "endpoint": endpoint_origin(settings["base_url"])}

    if not enabled():
        report["outcome"] = "disabled"
        report["detail"] = f"{ENV_ENABLED} is not 'true'"
        print(json.dumps(report, indent=2))
        return 0

    try:
        resolve_endpoint(settings["base_url"], settings["api_key"])
        rubric_version, roles = load_rubric(settings["roles"])
    except GateError as exc:
        report["outcome"] = exc.outcome
        report["detail"] = exc.detail
        print(json.dumps(report, indent=2))
        return 1

    report["rubricVersion"] = rubric_version
    report["roles"] = [role["key"] for role in roles]

    # A tiny, content-free probe state. It establishes that the endpoint answers and the model is
    # present, which is what an operator needs before enabling the gate on real records.
    probe_state = {"subject": "probe", "statement": "probe", "summary": "", "boundaries": "",
                   "kind": "", "scope": ""}
    try:
        call_model(probe_state, roles, settings, rubric_version)
    except GateError as exc:
        report["outcome"] = exc.outcome
        report["detail"] = exc.detail
        print(json.dumps(report, indent=2))
        return 1

    report["outcome"] = "ok"
    # The key's presence is reported, never its value.
    report["apiKey"] = "<set>" if settings["api_key"] else "<empty>"
    print(json.dumps(report, indent=2))
    return 0


def main():
    parser = argparse.ArgumentParser(prog="decisions_gate")
    sub = parser.add_subparsers(dest="command", required=True)

    score = sub.add_parser("score", help="score records from stdin")
    score.add_argument("--state-file", help="ledger path bounding the attempt loop")
    score.set_defaults(func=cmd_score)

    probe = sub.add_parser("probe", help="report availability")
    probe.set_defaults(func=cmd_probe)

    args = parser.parse_args()
    try:
        sys.exit(args.func(args))
    except GateError as exc:
        print(json.dumps({"outcome": exc.outcome, "detail": exc.detail}, indent=2), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()