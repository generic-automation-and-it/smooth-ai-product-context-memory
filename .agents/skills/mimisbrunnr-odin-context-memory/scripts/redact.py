#!/usr/bin/env python3
"""Fingerprint secret-redaction detector for the mimisbrunnr-odin-context-memory skill.

Reads a batch of candidate content strings on stdin (JSON array), scrubs the recognised secret spans,
and emits the redacted batch plus a findings list on stdout. A finding reports the rule NAME and the
character offsets of each replaced span — never the matched text, never the content around it. Content
flows via stdin/stdout so it never appears in argv (visible to ps/logs). Redact-and-flag (LADR-003): a
leak is scrubbed and recorded, never a reason to reject the fact.

Every scrub is visible. The write path's digest names the field it altered and the offsets it replaced,
so a redaction that hits ordinary prose is something the caller can see and correct, not a silent edit
to a stored statement.

WHAT THIS GATES, PRECISELY. The rules are fixed-shape fingerprints, not semantic detection. The `set`
path now scrubs through them automatically and refuses to write when the scrubber cannot run — that is
a **fail-closed gate on recognition**, and it is easy to describe as more than it is:

- It catches a value that *matches* a known form: `AKIA…`/`ASIA…`, `gh[pousr]_…`/`github_pat_…`,
  `sk-…`/`sk-proj-…`/`sk-ant-…`, a JWT, a PEM private-key block (any label, terminated or not), URL
  userinfo (`scheme://user:pass@host`), an `Authorization:` or `Bearer` credential, an assignment to a
  key whose name says secret (`password=`, `"clientSecret": "…"`, `ApiAccess__WriteToken=`,
  `CONTEXT_MEMORY_WRITE_TOKEN=`, quoted values with spaces included), or an assignment to a neutral key
  (`key`, `token`, `credential`) whose value is itself secret-shaped.
- It does **not** catch a secret that matches no rule. A bare high-entropy value, a base64 blob with
  no label, an unusual vendor token format, or a password in prose all pass through untouched.

So the correct claim is "recognisable secrets are gated, and the gate never silently fails open" — not
"sensitive material never reaches storage". BR-03's business decision about whether such material may
be stored at all is a separate, still-open product question; this is the mechanical half of it, and the
mechanical half is bounded by the rule set above. Widening the rules is the lever if the gap matters;
that is a deliberate, separate decision rather than something this module does implicitly.
"""

import argparse
import bisect
import json
import re
import sys

PLACEHOLDER = "<redacted>"

# Characters a token-shaped value is built from: alphanumerics plus the base64/base64url extras.
_TOKEN_RUN = re.compile(r"[A-Za-z0-9+/]{16,}")


def _secret_shaped(value):
    """True if `value` carries an unbroken run of 16+ token characters mixing letters and digits.

    Used only where the key name alone does not say "secret" (`key`, `token`, `credential`). Ordinary
    engineering prose puts identifiers there — `sort key = created_on`, `partition key: groupUuid`,
    `idempotency key = order-123`, a UUID — and those are made of short, separator-delimited words or
    digit-only runs. A generated secret is not: it is a long run of mixed letters and digits.
    """
    return any(re.search(r"[A-Za-z]", run) and re.search(r"[0-9]", run)
               for run in _TOKEN_RUN.findall(value))


def _not_already_redacted(value):
    return not value.strip("\"'").startswith("<redacted")


# Each rule is (name, pattern, value_group, placeholder, accept).
#
# `value_group` is the group whose span is replaced; 0 replaces the whole match. A rule that keeps a
# label (`password=`) puts the label outside that group, so the label survives and only the value is
# replaced — never the other way round. `accept`, where present, is a predicate over the value group's
# text that can decline a match the pattern alone would take.
#
# Order is priority: rules are matched against the ORIGINAL text, and a match overlapping one already
# accepted by an earlier rule is dropped. That is what lets the digest report positions in the text the
# caller actually holds, rather than in an intermediate string a previous rule already rewrote.
_PEM_LABEL = r"(?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?"

# A value after `key = ` / `key: `. Quoted values may contain spaces (a passphrase is still a secret
# past its first word); unquoted ones stop at whitespace, a quote, `,` or `;`.
_QUOTED = r""""[^"\r\n]{1,512}"|'[^'\r\n]{1,512}'"""


def _assignment(key, min_unquoted):
    """`<key>` then `:`/`=`, then a quoted or unquoted value as group 1.

    The key may be quoted (`"apiKey": …`), so a closing quote is allowed before the separator. The
    lookbehind pins the key to the start of an identifier, which also keeps the search linear: an
    identifier is tried once from its start rather than from every character inside it.
    """
    return re.compile(
        r"(?i)(?<![A-Za-z0-9_.-])(?:" + key + r")[\"']?\s*[:=]\s*"
        r"(" + _QUOTED + r"|[A-Za-z0-9._/+@!#$%&*?~-]{" + str(min_unquoted) + r",}={0,2})"
    )


RULES = [
    (
        "aws-access-key-id",
        re.compile(r"\b((?:AKIA|ASIA)[0-9A-Z]{16})\b"),
        0, "<redacted-aws-access-key>", None,
    ),
    (
        "github-token",
        re.compile(r"\b(gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{22,255})\b"),
        0, "<redacted-github-token>", None,
    ),
    (
        # The body may not contain `-----`, so a BEGIN with no END stops at the next marker instead of
        # scanning to the end of the text. The unbounded `.*?` this replaces was quadratic in the number
        # of unterminated BEGIN markers (8 000 of them took 4.4 s).
        "private-key-pem",
        re.compile(
            r"-----BEGIN " + _PEM_LABEL + r"-----(?:[^-]|-(?!----))*?-----END " + _PEM_LABEL + r"-----"
        ),
        0, "<redacted-private-key>", None,
    ),
    (
        # A truncated or unterminated block is still key material. Takes the BEGIN line plus the
        # base64 and `Header: value` lines that follow it — never the prose after them.
        "private-key-pem",
        re.compile(
            r"-----BEGIN " + _PEM_LABEL + r"-----"
            r"(?:\s+(?:[A-Za-z0-9+/]{16,}={0,2}|[A-Za-z0-9+/]{0,15}={1,2}|[A-Z][A-Za-z-]*:[^\r\n]*))*"
        ),
        0, "<redacted-private-key>", None,
    ),
    (
        "jwt",
        re.compile(r"(?<![A-Za-z0-9_-])(eyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*)"),
        0, "<redacted-jwt>", None,
    ),
    (
        # A qualified vendor prefix is the evidence on its own. Project and service-account keys use
        # `_`/`-`-separated bodies, which `_secret_shaped` reads as short words, so requiring it here
        # let `sk-proj-…` keys through unredacted.
        "api-key-sk",
        re.compile(r"(?<![A-Za-z0-9_-])(sk-(?:proj|ant|live|test|svcacct|admin)-[A-Za-z0-9_-]{16,}"
                   r"|[sr]k_(?:live|test)_[A-Za-z0-9]{16,})"),
        0, "<redacted-api-key>", None,
    ),
    (
        # A bare `sk-` is ambiguous with prose (`sk-learn-compatible-…`), so it still needs the value
        # itself to look generated.
        "api-key-sk",
        re.compile(r"(?<![A-Za-z0-9_-])(sk-[A-Za-z0-9_-]{20,})"),
        0, "<redacted-api-key>", _secret_shaped,
    ),
    (
        "url-userinfo",
        re.compile(r"(?i)\b[a-z][a-z0-9+.-]{0,31}://[^\s/:@?#]*:([^\s/@?#]+)@"),
        1, PLACEHOLDER, _not_already_redacted,
    ),
    (
        "authorization-header",
        re.compile(r"(?i)\bauthorization[\"']?\s*[:=]\s*[\"']?(?:bearer|basic|token|digest)\s+"
                   r"([A-Za-z0-9._~+/=-]{8,})"),
        1, PLACEHOLDER, _not_already_redacted,
    ),
    (
        "authorization-header",
        re.compile(r"(?i)\bbearer\s+([A-Za-z0-9._~+/=-]{16,})"),
        1, PLACEHOLDER, _secret_shaped,
    ),
    (
        "connection-string-password",
        re.compile(r"(?i)(?:password|pwd)\s*=\s*(" + _QUOTED + r"|[^'\";\s&]+)"),
        1, PLACEHOLDER, _not_already_redacted,
    ),
    (
        "aws-secret-access-key",
        re.compile(r"(?i)\baws_secret_access_key\s*=\s*(" + _QUOTED + r"|\S+)"),
        1, PLACEHOLDER, None,
    ),
    (
        # A key whose NAME says it holds a secret. Any value of eight or more characters is taken:
        # the name is the evidence, and a false positive here costs a placeholder, not a leak.
        # camelCase (`apiKey`, `clientSecret`, `writeToken`) and env-style (`ApiAccess__WriteToken`,
        # `CONTEXT_MEMORY_WRITE_TOKEN`) keys are covered by the suffix match.
        "generic-secret-assignment",
        _assignment(
            r"[A-Za-z0-9_.-]*(?:secret|passwd|password|passphrase|api[_-]?key|access[_-]?key"
            r"|private[_-]?key|[A-Za-z0-9][_.-]*token)",
            8,
        ),
        1, PLACEHOLDER, _not_already_redacted,
    ),
    (
        # A key whose name does NOT say secret — bare `key`/`token`, `sort_key`, `credential` — is
        # redacted only when the value itself is secret-shaped. This is the rule that used to turn
        # `sort key = created_on` into `sort key = <redacted>`.
        "generic-secret-assignment",
        _assignment(r"[A-Za-z0-9_.-]*key|token|credentials?", 16),
        1, PLACEHOLDER, _secret_shaped,
    ),
]

# Every free-text field on a `set` payload that reaches durable storage. The blob body is the one
# that cannot be repaired — content addressing makes it immutable, so a secret in it can only be
# orphaned, never edited out — but the memory_version columns are durable too, and a row is as
# hard to scrub as a blob once written. `kind`, `status` and `confidence` are omitted: they are
# closed enums validated server-side, and nothing free-form reaches them.
SET_TEXT_FIELDS = (
    "name",
    "description",
    "statement",
    "contentSummary",
    "content",
    "summaryModel",
    "summaryPromptVersion",
)
SET_LIST_FIELDS = ("facets", "tags")
SET_SOURCE_FIELDS = ("kind", "reference")


def scrub_located(content):
    """Return (redacted_content, hits) where hits is [(rule_name, start, end)] in `content` order.

    `start`/`end` are code-point offsets into the ORIGINAL `content`, half-open, covering exactly the
    characters that were replaced. They locate a redaction without restating it: the span's text is
    never retained, logged or returned.
    """
    # Accepted matches, kept sorted by start and mutually disjoint, so an overlap test is two
    # neighbour comparisons rather than a scan — a scan made 8 000 PEM markers quadratic again.
    starts, taken = [], []  # taken: (start, end, rule_name, value_start, value_end, replacement)
    for name, pattern, group, placeholder, accept in RULES:
        pos = 0
        while pos <= len(content):
            match = pattern.search(content, pos)
            if match is None:
                break
            start, end = match.span()
            value_start, value_end = match.span(group)
            index = bisect.bisect_left(starts, start)
            overlaps = ((index > 0 and taken[index - 1][1] > start)
                        or (index < len(taken) and taken[index][0] < end))
            if overlaps or (accept is not None and not accept(content[value_start:value_end])):
                pos = start + 1
                continue
            starts.insert(index, start)
            taken.insert(index, (start, end, name, value_start, value_end, placeholder))
            pos = end if end > start else end + 1
    out, cursor, hits = [], 0, []
    for _start, _end, name, value_start, value_end, replacement in taken:
        out.append(content[cursor:value_start])
        out.append(replacement)
        cursor = value_end
        hits.append((name, value_start, value_end))
    out.append(content[cursor:])
    return "".join(out), hits


def _scrub(content):
    """Return (redacted_content, findings). findings is a dict {rule_name: hit_count}."""
    redacted, hits = scrub_located(content)
    findings = {}
    for name, _start, _end in hits:
        findings[name] = findings.get(name, 0) + 1
    return redacted, findings


def counts(hits):
    """{rule_name: hit_count} over a list of located hits."""
    total = {}
    for hit in hits:
        total[hit["rule_name"]] = total.get(hit["rule_name"], 0) + 1
    return total


def digest(hits):
    """The write-path digest: per rule, a count plus where each redaction happened.

    Shape: [{rule_name, hit_count, locations: [{field, start, end}]}], sorted by rule name. A
    location says which field of the request was altered and which characters of the caller's own
    text were replaced, so a scrub is never silent — but it carries offsets, never the replaced text.
    """
    by_rule = {}
    for hit in hits:
        by_rule.setdefault(hit["rule_name"], []).append(
            {"field": hit["field"], "start": hit["start"], "end": hit["end"]})
    return [{"rule_name": name, "hit_count": len(locations), "locations": locations}
            for name, locations in sorted(by_rule.items())]


def _scrub_text(value, path, hits):
    """Scrub one string; record each replacement against its field path."""
    if not isinstance(value, str):
        return value
    clean, located = scrub_located(value)
    hits.extend({"field": path, "rule_name": name, "start": start, "end": end}
                for name, start, end in located)
    return clean


# Spec marker for "this value is free text": a string here is scrubbed, anything else is left alone.
TEXT = "text"


def _set_spec():
    """The `set` body's free-text fields, built from the tuples above at call time."""
    item = {field: TEXT for field in SET_TEXT_FIELDS}
    item.update({field: [TEXT] for field in SET_LIST_FIELDS})
    item["sources"] = [{field: TEXT for field in SET_SOURCE_FIELDS}]
    return {"items": [item], "links": [{"reason": TEXT}], "labelsProposed": [TEXT]}


def _fold(name):
    """Fold a JSON key the way the Host matches it — case-insensitively.

    The Host binds request JSON with case-insensitive property names, so `Statement`, `STATEMENT` and
    `statement` all land in the same column. Matching the spec exact-case let `{"Content": "token=…"}`
    through unscrubbed. Upper-then-casefold is a superset of an ordinal ignore-case comparison: it can
    only match more keys than the Host does, never fewer, and an extra scrub is the safe direction.
    """
    return name.upper().casefold()


def _walk(value, spec, path, hits):
    """Return a scrubbed copy of `value` per `spec`; never mutates the input."""
    if spec == TEXT:
        return _scrub_text(value, path, hits)
    if isinstance(spec, list):
        if not isinstance(value, list):
            return value
        return [_walk(entry, spec[0], f"{path}[{index}]", hits) for index, entry in enumerate(value)]
    if not isinstance(value, dict):
        return value
    lookup = {_fold(key): sub for key, sub in spec.items()}
    clean = {}
    for key, entry in value.items():
        sub = lookup.get(_fold(key)) if isinstance(key, str) else None
        clean[key] = entry if sub is None else _walk(entry, sub, f"{path}.{key}" if path else key, hits)
    return clean


def scrub_payload(payload, spec):
    """Return (scrubbed_payload, hits) for a request body described by `spec`.

    Returns a **new** payload built from new containers; the input and every object reachable
    from it are left untouched, so a caller holding the original — a dry-run comparison, a
    retry, a test asserting what it planted — still has the value it passed in. Rebuilding is
    what makes that true: mutating the items in place would scrub the caller's dict too, because
    a shallow copy shares them.

    `hits` is a list of {field, rule_name, start, end} — `field` is the JSON path of the altered
    string (`items[0].statement`), `start`/`end` the replaced characters' offsets in it. `digest()`
    turns it into what the write path reports. The matched text is never retained, logged or
    returned.

    Keys are matched case-insensitively (see `_fold`). A non-dict payload is returned unchanged with
    no hits: every caller validates shape first and refuses a bad payload on its own terms, so this
    never has to decide.
    """
    hits = []
    if not isinstance(payload, dict):
        return payload, hits
    return _walk(payload, spec, "", hits), hits


def scrub_set_payload(payload):
    """`scrub_payload` over the `set` body (see `SET_TEXT_FIELDS` and siblings)."""
    return scrub_payload(payload, _set_spec())


# The free-text fields of every other write body, per operation. Each is persisted — a group's
# name/body as an append-only description, a link reason, a label or initiative name, a ticket
# declaration's reason and source — so each is gated exactly like `set`.
#
# Ticket identities (`provider`/`key`, and `child`/`parent`/`expectedParent` on a hierarchy
# declaration) are deliberately not walked: they are matched exactly against stored rows, so
# rewriting one would bind the write to a different ticket. A ticket's `url` is free text and is.
_GROUP_FIELDS = {"repo": TEXT, "repoUrl": TEXT, "initiativeName": TEXT, "scopeIdentifier": TEXT,
                 "tickets": [{"url": TEXT}]}
WRITE_SPECS = {
    "resolve_group": dict(_GROUP_FIELDS, name=TEXT, body=TEXT),
    "update_group": dict(_GROUP_FIELDS),
    "append_description": {"name": TEXT, "body": TEXT},
    "create_link": {"reason": TEXT},
    "ticket_parent": {"reason": TEXT, "source": TEXT},
    "propose_label": {"name": TEXT},
    "upsert_initiative": {"name": TEXT, "description": TEXT},
}


def scrub_write_payload(operation, payload):
    """Scrub the body of write `operation`. An operation with no declared spec raises, so a new
    write tool cannot reach the store unscrubbed by being left out of the table."""
    if operation == "set":
        return scrub_set_payload(payload)
    return scrub_payload(payload, WRITE_SPECS[operation])


def findings_for(located):
    """Per-string findings: [{rule_name, hit_count, spans: [{start, end}]}], sorted by rule name."""
    by_rule = {}
    for name, start, end in located:
        by_rule.setdefault(name, []).append({"start": start, "end": end})
    return [{"rule_name": name, "hit_count": len(spans), "spans": spans}
            for name, spans in sorted(by_rule.items())]


def main():
    parser = argparse.ArgumentParser(prog="redact")
    parser.add_argument("--input", help="JSON file of content strings; defaults to stdin")
    args = parser.parse_args()

    if args.input:
        with open(args.input, "r", encoding="utf-8") as fh:
            batch = json.load(fh)
    else:
        batch = json.load(sys.stdin)

    if not isinstance(batch, list):
        print("redact: expected a JSON array on stdin", file=sys.stderr)
        sys.exit(1)
    if not all(isinstance(item, str) for item in batch):
        print("redact: array items must be strings", file=sys.stderr)
        sys.exit(1)

    results = []
    for index, content in enumerate(batch):
        redacted, located = scrub_located(content)
        results.append(
            {
                "candidate_index": index,
                "redacted": redacted,
                "findings": findings_for(located),
            }
        )

    print(json.dumps({"results": results}, indent=2))


if __name__ == "__main__":
    main()
