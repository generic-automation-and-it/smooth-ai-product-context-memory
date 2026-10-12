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
- **The attempt counter is the script's, not the caller's.** A ledger keyed by a SHA-256 digest of the
  record identity in a state file bounds the rewrite loop, so an agent cannot reset it by re-asking —
  and the subject, which can carry personal data, never reaches the file.
- **The score is a quality signal, never authority.** It never changes status, kind, or approval.

Python 3.9+, stdlib only.
"""

import argparse
import hashlib
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
DEFAULT_MIN_PROBABILITY = 0.85
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
# Not one of the ten operator settings: a test seam for the ledger cap, documented in
# `ledger_cap`. It exists because the shipped 5000 cannot be reached over HTTP in a suite.
ENV_LEDGER_MAX = "CONTEXT_MEMORY_DECISIONS_LEDGER_MAX_ENTRIES"

SCRIPTS = Path(__file__).resolve().parent
REDACTOR = SCRIPTS / "redact.py"
RUBRIC = SCRIPTS / "decisions_rubric.json"

# These are seeded from the machine credential file at import, so the settings `run.sh` publishes are
# actually visible to the gate. Without it the gate read only the live process environment, while
# `CONTEXT_MEMORY_READ_TOKEN` and `CONTEXT_MEMORY_BASE_URL` are auto-loaded from that same file — making
# the decision settings the only ones in the skill its own operator tooling could not reach.
#
# The loader is **duplicated, not imported**, and that is deliberate. `context_memory_client` does
# `import redact` at module scope, so importing it here would make the redactor a startup dependency of
# the gate — and any test that substitutes a stub redactor would have that stub execute at import and
# consume the gate's own stdin. This mirrors the arrangement the two capture clients already use for the
# recall notice: one wording, kept byte-identical, with a test asserting the two agree rather than one
# importing the other. `load_machine_credentials_agrees` in the harness is that test.
#
# Contract, matching the capture client exactly: named keys only, a value already in the environment
# always wins, and a malformed line is skipped rather than raised so a partial file cannot turn one
# working variable into an import-time crash.
MACHINE_CREDENTIAL_FILE = os.environ.get(
    "CONTEXT_MEMORY_CREDENTIAL_FILE",
    os.path.join(os.path.expanduser("~"), ".mimisbrunnr", "credentials"))


def load_machine_credentials(*names):
    """Seed the named variables from the machine credential file when they are not already set.

    Returns the file it read, or None.
    """
    try:
        with open(MACHINE_CREDENTIAL_FILE, encoding="utf-8") as handle:
            lines = handle.readlines()
    except OSError:
        return None
    wanted = set(names)
    for line in lines:
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        key = key.strip()
        # Presence, not truthiness: an explicitly set empty value is the operator turning a setting
        # off — `CONTEXT_MEMORY_DECISIONS_ENABLED=` must not be re-enabled from the file (review #4).
        if key in wanted and value and key not in os.environ:
            os.environ[key] = value.strip()
    return MACHINE_CREDENTIAL_FILE


load_machine_credentials(
    ENV_ENABLED, ENV_BASE_URL, ENV_PATH, ENV_MODEL, ENV_API_KEY,
    ENV_MIN_PROBABILITY, ENV_MAX_ATTEMPTS, ENV_ROLES, ENV_BELOW_THRESHOLD, ENV_TIMEOUT,
)

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

# How long the redactor subprocess may run. A redactor that overruns it is `redactor-unavailable`
# like any other redactor that cannot run: no request is made.
REDACTOR_TIMEOUT_SECONDS = 60


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

    # Validated here rather than at first use so a malformed cap is refused once, up front, from the
    # same place every other bad setting is refused — and so it cannot surface as a traceback from
    # inside `record_attempt`, which is not under the per-record handler.
    ledger_cap()

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
        # Read inside the handler: `.port` is lazy and raises on `:99999` or `:abc`, which escaped as a
        # traceback from `probe` and from the end of `score` — before the endpoint guard could refuse it
        # as `bad-decisions-url` (issue 184).
        port = parsed.port
    except ValueError:
        return "<unparseable>"
    host = parsed.hostname or "<no-host>"
    if ":" in host and not host.startswith("["):
        host = f"[{host}]"
    # Only a scheme the guard accepts is shown: `admin:hunter2` parses with scheme `admin`, so a
    # credential pasted into the variable was echoed back in every report (issue 188).
    scheme = parsed.scheme if parsed.scheme in ("http", "https") else "<invalid-scheme>"
    return f"{scheme}://{host}:{port}" if port else f"{scheme}://{host}"


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
        # The scheme is not echoed: `admin:hunter2` parses with scheme `admin` (issue 188).
        raise GateError("bad-decisions-url", "the decision endpoint must be http or https")
    if not parsed.hostname:
        # `http:///v1` and `http://:11434` parse with no host; urllib would then resolve one of its
        # own choosing, so there is nothing to validate the destination against (issue 188).
        raise GateError("bad-decisions-url", "the decision endpoint must name a host")
    if parsed.params or parsed.query or parsed.fragment or any(mark in base_url for mark in "?#;"):
        # The path is appended after the base, so `;tok=x`, `?k=v` or `#f` would ride along with
        # every request, or swallow the path. Never echoed: the parameter may be a pasted credential.
        raise GateError("bad-decisions-url",
                        "the decision endpoint URL must not carry ';' parameters, a query or a fragment")
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


def _field_text(value):
    """A record field as the text the redactor inspects and the model receives.

    A string passes through. Anything else that carries content — a number, a list, a nested map — is
    serialised to JSON first, so the redactor sees all of it, keys included: a nested
    `{"password": "…"}` is caught by the quoted-key rule only while the key sits beside its value, and
    scrubbing leaves one by one would lose that. Before this the non-string value skipped the redactor
    and reached the model unscrubbed (issue 184). Never iterated: a string is iterable too, and a field
    walked element by element is how one malformed value becomes several plausible ones.
    """
    if isinstance(value, str):
        return value
    return json.dumps(value, ensure_ascii=False, sort_keys=True)


def record_state(record):
    """The record as exported, as the model's `state`.

    Only the fields a reader would judge value from. Deliberately not the whole payload: a uuid, a
    timestamp and an internal flag tell the model nothing about whether this is worth keeping. Every
    value is text (`_field_text`), so every value passes through the redactor.
    """
    state = {
        "subject": record.get("subject") or record.get("description") or record.get("name") or "",
        "statement": record.get("statement") or record.get("answer") or "",
        "summary": record.get("contentSummary") or record.get("why") or "",
        "boundaries": record.get("boundaries") or record.get("validUntil") or "",
        "kind": record.get("kind") or "",
        "scope": record.get("scope") or record.get("scopeDimension") or "",
    }
    return {field: _field_text(value) for field, value in state.items()}


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
            capture_output=True, text=True, encoding="utf-8", timeout=REDACTOR_TIMEOUT_SECONDS,
        )
    # `SubprocessError` covers `TimeoutExpired`. Uncaught, a hung redactor escaped as a traceback,
    # which the kvasir caller read as a skipped gate and wrote the record unscored (issue 182).
    except (OSError, ValueError, subprocess.SubprocessError) as exc:
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
    seen = set()
    for item in results:
        if not isinstance(item, dict):
            raise GateError("redactor-unavailable",
                            "the redactor returned an unexpected shape; no request was made")
        # Pair by the returned `candidate_index`, and require every index exactly once. Pairing by
        # position trusted the order: a reordered answer put one field's scrubbed text in another's
        # slot, so a field could be sent carrying text the redactor had never been asked to inspect
        # in that position (issue 186). Any index that is missing, repeated, out of range or not an
        # integer means the mapping is untrustworthy, and that refuses.
        index = item.get("candidate_index")
        if type(index) is not int or not 0 <= index < len(unique) or index in seen:
            raise GateError("redactor-unavailable",
                            "a redactor result carried no usable candidate_index; no request was made")
        seen.add(index)
        if not isinstance(item.get("redacted"), str):
            # Absent is not "unchanged". Defaulting to "" would blank the field, and defaulting to
            # the original would send it; neither is an inspection, so this refuses — as does a
            # non-text value, which is not a scrub of the text that was sent.
            raise GateError("redactor-unavailable",
                            "a redactor result carried no text 'redacted' field; no request was made")
        scrubbed[unique[index]] = item["redacted"]
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
            if value in scrubbed:
                state[field] = scrubbed[value]
            elif value:
                # Every non-empty field was sent to the redactor above; one that has no scrubbed
                # counterpart was never inspected, and an uninspected field is never sent.
                raise GateError("redactor-unavailable",
                                f"field {field!r} was not inspected by the redactor; no request was made")
        out.append(state)
    return out, findings


def estimate_tokens(text):
    """A deliberately pessimistic token estimate. See CHARS_PER_TOKEN."""
    return math.ceil(len(text) / CHARS_PER_TOKEN)


def size_guard(state, roles, model):
    """Refuse an oversize request BEFORE it is sent. Never truncate to fit.

    Sized on the request body `call_model` sends, not on the record alone: every role's instructions
    and criteria travel with the record, so a record that fits on its own could still overflow the
    model's context once the rubric was added (issue 182).
    """
    request_text = encode_request(state, roles, model).decode("utf-8")
    total = estimate_tokens(request_text)
    body = len(request_text.encode("utf-8"))
    if total > MAX_CONTEXT_TOKENS:
        return (f"request estimates to ~{total} tokens against a {MAX_CONTEXT_TOKENS}-token context "
                f"({len(request_text)} chars, rubric included)")
    if body > MAX_BODY_BYTES:
        return f"request body is {body} bytes against a {MAX_BODY_BYTES}-byte limit"
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


def encode_request(state, roles, model):
    """The exact bytes `call_model` sends, shared with `size_guard` so the two cannot disagree."""
    return json.dumps(build_request(state, roles, model), ensure_ascii=False).encode("utf-8")


def _classify(exc, timeout):
    """A transport failure named, never a score. `timed-out` is kept apart from `unreachable`
    because a hang is worth retrying and a refusal is an operator action — the same distinction the
    store's read path makes, and for the same reason: a hung model must never read as a low score."""
    if isinstance(exc, (TimeoutError, socket.timeout)):
        return GateError("timed-out", f"no answer within {timeout}s")
    return GateError("unreachable", "nothing accepted a connection")


def discrimination(scores):
    """How decisively the highest-scoring role beat the field. `(margin, tied)`.

    Added because `passingRoles` is consumed downstream as `audience:*` tags, and on its own it
    cannot distinguish two very different situations. A record three roles scored at exactly
    `1.00000` yields three tags and reads as though three roles independently cleared the bar,
    which is indistinguishable from a record where one role led and the rest trailed. The scores
    are near-saturated at the top — measured mean best-role 0.99 on the calibration fixture — so
    exact ties are common rather than exceptional.

    `margin` is the gap between the highest score and the highest *other* score, so it is 0.0 for
    a tie and 1.0 for a unanimous sweep. `tied` names every role within a float epsilon of the
    top. Neither changes a verdict: `passing` is still computed from the raw scores alone, and
    this only reports how much to trust the shape of `passing`.
    """
    if not scores:
        return 0.0, []
    ordered = sorted(scores.values(), reverse=True)
    top = ordered[0]
    margin = top - ordered[1] if len(ordered) > 1 else top
    tied = [role for role, value in scores.items() if top - value <= 1e-9]
    return margin, sorted(tied)


def request_path(path):
    """The configured request path, refused unless it can only extend the validated origin.

    The path is appended to a base URL that `resolve_endpoint` has already approved, so it can move the
    request as surely as the base can: `@other.example/v1` turns `http://localhost:11434` into a URL
    whose userinfo is `localhost:11434` and whose host is other.example — and the record and the bearer
    key go with it. Only an absolute path with no authority-bearing character passes. Same rule as
    muninn's `recall_feedback_path_ok`. The path is never echoed: it is configuration that may carry a
    pasted credential.
    """
    if not path.startswith("/") or path.startswith("//"):
        raise GateError("bad-decisions-url",
                        f"{ENV_PATH} must be an absolute path starting with a single '/'")
    if "@" in path or "\\" in path or any(ch.isspace() or ord(ch) < 0x20 or ord(ch) == 0x7F
                                           for ch in path):
        raise GateError("bad-decisions-url",
                        f"{ENV_PATH} must not carry '@', '\\', whitespace or a control character")
    if any(mark in path for mark in "?#;"):
        # A query, fragment or `;` parameter in the path rides along with every request, and a pasted
        # `?token=…` is the shape a credential most often takes there (issue 188).
        raise GateError("bad-decisions-url",
                        f"{ENV_PATH} must not carry a query, a fragment or ';' parameters")
    return path


def _authority(parsed):
    return parsed.scheme.lower(), parsed.hostname, parsed.port


def resolve_url(settings):
    """The full request URL, asserted to reach the same scheme, host and port as the approved base.

    The path rules above are the first net; this is the second, judged on the joined URL itself, so a
    path shape the rules did not anticipate still cannot change the destination.
    """
    base = resolve_endpoint(settings["base_url"], settings["api_key"])
    url = base + request_path(settings["path"])
    try:
        same = _authority(urlparse(url)) == _authority(urlparse(base))
    except ValueError:
        same = False
    if not same:
        raise GateError("bad-decisions-url",
                        f"{ENV_PATH} changes the request's destination; refusing to send")
    return url


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """Refuse every redirect. The request carries the record and, for a hosted endpoint, the bearer
    key; urllib would replay both to whatever `Location` names. Same guard as the store client's."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise GateError("redirect-refused",
                        f"the decision endpoint answered {code} with a redirect; credential-bearing "
                        "requests do not follow redirects")


def call_model(state, roles, settings, rubric_version):
    """Score one record. Returns scores, passing roles, and the rubric version recorded with them."""
    body = encode_request(state, roles, settings["model"])
    url = resolve_url(settings)

    headers = {"Content-Type": "application/json", "Accept": "application/json"}
    # Sent only when non-empty: local Ollama needs no key, and an empty Authorization header is a
    # different request from no header.
    if settings["api_key"]:
        headers["Authorization"] = f"Bearer {settings['api_key']}"

    request = urllib.request.Request(url, data=body, headers=headers, method="POST")
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoRedirect)
    try:
        with opener.open(request, timeout=settings["timeout"]) as response:
            raw = response.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        detail = (exc.read().decode("utf-8", errors="replace") or "").strip()
        # A missing model is the one HTTP failure an operator can act on without reading a traceback.
        # Only a 404 — or a 400 whose body names the model as not found — reads as missing. Any
        # status whose body merely mentions "model" (a 500 from inside the model server, say) was
        # reported as a missing model, sending the operator to pull a model that was there (review #19).
        if exc.code == 404 or (exc.code == 400 and re.search(r"model\b.*\bnot found|no such model",
                                                             detail, re.IGNORECASE)):
            raise GateError("model-missing",
                            f"the decision model is not available ({exc.code})") from None
        # The body is read only to classify, never echoed: the endpoint may be hosted, and its error
        # text — which can quote the request, the key's account, or anything else it chooses — went
        # straight into the report and the export's transcript (issue 186). Its size is the signal.
        raise GateError(f"http-{exc.code}",
                        f"the decision endpoint refused the request (HTTP {exc.code}"
                        + (f"; a {len(detail)}-character body was not shown)" if detail else ")")
                        ) from None
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
        # Refused, not clamped. `json.loads` accepts `NaN` and `Infinity`, and clamping turned an
        # `Infinity` (or a 1.5) into a confident 1.0 that passes any bar — a malformed answer reported
        # as the strongest possible score. A probability outside 0..1 is not a score at all.
        try:
            probability = float(value)
        except OverflowError:
            probability = math.inf
        if not math.isfinite(probability) or not 0.0 <= probability <= 1.0:
            raise GateError("bad-response",
                            f"role {role['key']!r} carried a `noul` outside 0..1")
        scores[role["key"]] = probability

    passing = [role for role, value in scores.items() if value > settings["min_probability"]]
    return scores, passing, rubric_version


# --------------------------------------------------------------------------- attempt ledger


def ledger_path(state_file):
    return Path(state_file)


LEDGER_KEY_PREFIX = "sha256:"
_LEDGER_KEY = re.compile(r"sha256:[0-9a-f]{64}")


def ledger_key(identity):
    """The on-disk key for a record identity: a SHA-256 digest, never the identity itself.

    The identity is the record's subject, and a subject can carry personal data — a name, an email, a
    customer. The ledger only needs to recognise the same record again, which a digest does as well as
    the raw text, so the raw text never has to reach the file.
    """
    return LEDGER_KEY_PREFIX + hashlib.sha256(identity.encode("utf-8")).hexdigest()


def _merge_entries(first, second):
    """One entry from two that now share a key: the larger spent count and the better best."""
    if first is None:
        return second
    best_first, best_second = entry_best(first), entry_best(second)
    best = best_first if best_second is None else best_attempt(best_first, best_second)
    return {"attempts": max(entry_attempts(first), entry_attempts(second)), "best": best}


def migrate_ledger(ledger):
    """Re-key a ledger written before keys were digests. Returns `(ledger, migrated)`.

    A raw-identity key is hashed rather than dropped: dropping it would reset that record's budget on
    upgrade, the same silent reset `ledgerReset` exists to expose. Two raw keys never hash alike, but a
    raw key can meet the digest of itself if an older and a newer gate both wrote this file; those are
    merged, keeping the larger spent count. The caller rewrites the file whenever `migrated` is true, so
    the raw subjects leave the disk on the first run after an upgrade.
    """
    migrated = {}
    changed = False
    for key, value in ledger.items():
        if not _LEDGER_KEY.fullmatch(key):
            key = ledger_key(key)
            changed = True
        migrated[key] = _merge_entries(migrated.get(key), value)
    return migrated, changed


def read_ledger(state_file):
    """The ledger, whether it had to be discarded, and whether it was re-keyed from raw identities.

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
    except FileNotFoundError:
        return {}, False, False
    except (OSError, ValueError):
        # Only a missing file is a first run. A ledger that exists and cannot be read (permissions, a
        # directory in its place, an I/O error) starts empty too, so it is a reset and is disclosed like a
        # corrupt one: an `OSError` returned "not discarded" and cleared every spent budget silently
        # (consumer review 5441621898 #6).
        return {}, True, False
    if not isinstance(data, dict):
        return {}, True, False
    # An entry is `{"attempts": int, "best": {...} | null}`, because the best attempt has to survive
    # between invocations for the comparison it exists to make. A **bare integer is also accepted** and
    # read as a count with no recorded best, so a ledger written by the earlier count-only format
    # keeps working instead of being discarded — which on upgrade would silently reset every budget,
    # the exact failure `ledgerReset` exists to make visible.
    for key, value in data.items():
        if not isinstance(key, str):
            return {}, True, False
        if isinstance(value, int) and not isinstance(value, bool) and value >= 0:
            continue
        if not isinstance(value, dict):
            return {}, True, False
        attempts = value.get("attempts")
        if not isinstance(attempts, int) or isinstance(attempts, bool) or attempts < 0:
            return {}, True, False
        if value.get("best") is not None and not valid_best(value["best"]):
            return {}, True, False
    ledger, migrated = migrate_ledger(data)
    return ledger, False, migrated


def _finite_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def valid_best(best):
    """A recorded best attempt in the shape `record_attempt` writes.

    Checking only that it was an object let `{}` or a non-numeric `max` through, and the next scored
    round raised `KeyError`/`TypeError` in `best_attempt` — a traceback instead of the disclosed reset a
    damaged ledger gets (review 5432012955 #11).
    """
    return (isinstance(best, dict)
            and type(best.get("attempt")) is int and best["attempt"] >= 1
            and _finite_number(best.get("max"))
            and isinstance(best.get("scores"), dict)
            and all(isinstance(k, str) and _finite_number(v) for k, v in best["scores"].items()))


def entry_attempts(value):
    """The attempt count of a ledger entry, in either supported format."""
    return value.get("attempts", 0) if isinstance(value, dict) else value


def entry_best(value):
    """The recorded best attempt of a ledger entry, or None when there is not one yet."""
    return value.get("best") if isinstance(value, dict) else None


def ledger_cap():
    """The entry cap, overridable so it can be exercised through the **real write path**.

    Reaching `MAX_LEDGER_ENTRIES` over HTTP needs 5000 subprocess round trips, so the only end-to-end
    case that fitted in a test suite ran 55 subjects — comfortably under the shipped cap — and deleting
    the `cap_ledger` call from `record_attempt` left every ledger test green. A bound no case crosses
    is not a bound the suite tests, and a ledger growing without bound is exactly the defect this cap
    exists to prevent. A test-only seam beats both a suite too slow to run and a case that asserts the
    helper while calling the helper.

    Read per call rather than resolved at import, so a malformed value is a classified refusal from
    the same place every other bad setting is refused, instead of an import-time crash. It is refused
    rather than clamped, on the same rule as every other numeric setting: a cap that silently becomes
    something other than what was asked for is one the operator trusts and the system does not honour.
    """
    raw = os.environ.get(ENV_LEDGER_MAX, "").strip()
    if not raw:
        return MAX_LEDGER_ENTRIES
    try:
        value = int(raw)
    except ValueError:
        raise GateError("bad-decisions-config",
                        f"{ENV_LEDGER_MAX} must be a positive integer; refusing to run with a "
                        "different cap") from None
    if value < 1:
        raise GateError("bad-decisions-config", f"{ENV_LEDGER_MAX} must be at least 1")
    return value


def cap_ledger(ledger, max_entries=None, keep=None):
    """Bound the ledger by dropping the most-spent entries once it grows past the cap.

    Nothing else prunes it: an entry is kept precisely because it must remember that its budget is
    spent, so a long session accumulates one entry per distinct subject it has ever scored — a
    20 000-subject session reaches ~430 KB of `{"subject": 3}` lines and keeps growing. Dropping the
    **most-spent** entries is the cheapest way to bound it: those are the records most likely to have
    been written already, and losing the memory of a spent budget is the least harmful entry to lose.

    `keep` is the key being written, and it is never evicted. Without that guarantee, a ledger at the
    cap evicted the entry it had just counted, so that record restarted at attempt 1 on every round and
    its budget never ran out (issue 182). Ties are broken by key so the eviction is deterministic.
    """
    if max_entries is None:
        max_entries = ledger_cap()
    if len(ledger) <= max_entries:
        return ledger
    ordered = sorted(((key, value) for key, value in ledger.items() if key != keep),
                     key=lambda pair: (entry_attempts(pair[1]), pair[0]))
    room = max_entries - (1 if keep in ledger else 0)
    capped = dict(ordered[:max(room, 0)])
    if keep in ledger:
        capped[keep] = ledger[keep]
    return capped


def write_capped_ledger(state_file, ledger, keep):
    """Cap, write, and return how many entries the cap evicted, so the caller can disclose it."""
    capped = cap_ledger(ledger, keep=keep)
    write_ledger(state_file, capped)
    return len(ledger) - len(capped)


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
    # A memory is `(group, subject)`, not the subject alone: the same subject captured for two groups is
    # two memories. Keying by subject alone made them share one attempt budget, so scoring one spent the
    # other's (issue 186). The group is `groupUuid` when the caller has it, else `group` — whatever
    # describes it before it exists (kvasir sends the binding the group will be resolved from). A
    # record carrying neither keeps the subject-only key, so existing ledgers are read unchanged.
    group = record.get("groupUuid") or record.get("group")
    if group:
        return json.dumps({"group": group, "subject": str(subject)}, sort_keys=True, ensure_ascii=False)
    return str(subject)


def next_attempt(state_file, identity, max_attempts):
    """How many scoring rounds this record has had, bounded by `max_attempts`.

    The counter lives here rather than in the caller's loop because an agent that is asked to
    "improve until it passes" will otherwise re-ask whenever the answer is inconvenient. Once the
    budget is spent, further attempts are refused rather than silently accepted.

    Returns `(attempt, reset, prior_best, evicted)`; `reset` is True when the ledger had to be
    discarded, which the caller surfaces so a cleared budget is never mistaken for a first attempt, and
    `evicted` counts the entries the cap dropped on this write.
    """
    ledger, discarded, migrated = read_ledger(state_file)
    key = ledger_key(identity)
    entry = ledger.get(key)
    # Read both parts of the entry BEFORE any write. Two separate writes were the first cut of this,
    # and the second one read back a file the first had already replaced: writing the bare count
    # discards the recorded best, so every rewrite looked like an improvement and the stored `best`
    # was whichever round ran last. One read, then one write that preserves both.
    prior_best = entry_best(entry)
    used = entry_attempts(entry) if entry is not None else 0
    if used >= max_attempts:
        evicted = 0
        if migrated:
            # Nothing else would rewrite the file on this path, and a spent budget is exactly the
            # entry that stays put — so the raw subjects would outlive the upgrade indefinitely.
            evicted = write_capped_ledger(state_file, ledger, key)
        return None, discarded, prior_best, evicted
    # The count is spent whether or not the model answers, so it is written now with the best carried
    # through untouched; `record_attempt` then raises the best without disturbing the count.
    ledger[key] = {"attempts": used + 1, "best": prior_best}
    evicted = write_capped_ledger(state_file, ledger, key)
    return used + 1, discarded, prior_best, evicted


def record_attempt(state_file, identity, attempt, scores):
    """Remember a scored attempt, keeping the best the ledger has seen for this record.

    Written separately from `next_attempt` because the count is spent whether or not the model
    answered — an `unreachable` round must still count against the budget, or a down model would
    grant unlimited attempts for free. Returns `(best, evicted)`.
    """
    ledger, _discarded, _migrated = read_ledger(state_file)
    key = ledger_key(identity)
    candidate = {"attempt": attempt, "max": max(scores.values()), "scores": scores}
    entry = ledger.get(key)
    # `next_attempt` may have replaced a dict entry with a bare count, so the best is read from what
    # survives here rather than from the value passed in. Counting up from the surviving count keeps a
    # ledger written in the earlier format converging instead of restarting.
    #
    # The `is not None` guard is load-bearing. The two calls are separate reads of the file, and an
    # entry can be gone by the second one — before `keep` existed, `cap_ledger` evicted the very
    # identity it had just counted, and another process sharing the file can still drop it. Then
    # `entry_attempts(None)` is `None` and `max(None, attempt)` raises `TypeError`.
    prior = entry_best(entry)
    prior_attempts = entry_attempts(entry) if entry is not None else 0
    ledger[key] = {"attempts": max(prior_attempts, attempt),
                   "best": best_attempt(prior, candidate)}
    best = ledger[key]["best"]
    evicted = write_capped_ledger(state_file, ledger, key)
    return best, evicted


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
    # The endpoint is configuration, not a per-record condition. Checked inside `call_model` only, an
    # invalid URL became each record's outcome, the gate exited 0, and the export kept every record
    # unscored and carried on — a misconfigured gate read as a skipped one (issue 190). Checked once
    # here, before any record is touched, it is a refusal like every other bad setting.
    resolve_url(settings)

    redacted, findings = redact_records(records)

    results = []
    ledger_reset = False
    ledger_evicted = 0
    for index, (original, state) in enumerate(zip(records, redacted)):
        oversize = size_guard(state, roles, settings["model"])
        if oversize:
            # `discrimination` is disclosed on every record, not only the scored ones: a consumer
            # reading `margin` must not KeyError on exactly the records whose outcome it most needs to
            # reason about. There is no score here, so the value is the zero a failed round already
            # reports rather than a computed margin that would look like a measurement.
            results.append({
                "index": index, "identity": record_identity(original, index), "attempt": 0,
                "outcome": "oversize", "detail": oversize, "scores": {}, "passingRoles": [],
                "passed": False, "best": None, "discrimination": {"margin": 0.0, "tied": []},
            })
            continue

        if args.state_file:
            attempt, this_reset, prior_best, evicted = next_attempt(args.state_file,
                                                                    record_identity(original, index),
                                                                    settings["max_attempts"])
            ledger_evicted += evicted
            # One reset anywhere in the batch is reported, since it is one file and one bound.
            ledger_reset = ledger_reset or this_reset
            if attempt is None:
                results.append({
                    "index": index, "identity": record_identity(original, index),
                    "attempt": settings["max_attempts"], "outcome": "attempts-exhausted",
                    "detail": f"the {settings['max_attempts']}-attempt budget is spent for this record",
                    "scores": {}, "passingRoles": [], "passed": False, "best": None,
                    # Same disclosure as an oversize or a failed round: the field is always present, so
                    # a consumer reading it never has to special-case the record that was held.
                    "discrimination": {"margin": 0.0, "tied": []},
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
                "passed": False, "best": None, "discrimination": {"margin": 0.0, "tied": []},
            })
            continue

        best = best_attempt(prior_best,
                        {"attempt": attempt, "scores": scores, "max": max(scores.values())})
        # Persist only once a score exists, so a failed round leaves the best untouched while still
        # having spent its attempt above.
        if args.state_file:
            stored, evicted = record_attempt(args.state_file, record_identity(original, index),
                                             attempt, scores)
            ledger_evicted += evicted
        else:
            stored = best
        margin, tied = discrimination(scores)
        results.append({
            "index": index, "identity": record_identity(original, index), "attempt": attempt,
            "outcome": "scored", "rubricVersion": version, "scores": scores,
            "passingRoles": passing, "passed": bool(passing),
            # How decisively one role beat the field. Disclosed because `passingRoles` alone
            # cannot distinguish three roles tied at 1.0 from three roles that each cleared the bar
            # on their own merits — and the top of this distribution is saturated enough that ties
            # are routine. Never affects `passed`.
            "discrimination": {"margin": round(margin, 6), "tied": tied},
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
        # Always present with a ledger: an evicted entry is a record whose spent budget was forgotten,
        # so the count says how many budgets this run restarted.
        payload["ledgerEvicted"] = ledger_evicted
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
        resolve_url(settings)
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