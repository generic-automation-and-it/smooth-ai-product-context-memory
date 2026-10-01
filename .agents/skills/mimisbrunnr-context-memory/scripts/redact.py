#!/usr/bin/env python3
"""Fingerprint secret-redaction detector for the mimisbrunnr-context-memory skill.

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

- It catches a value that *matches* a known form: `AKIA…`, `gh[pousr]_…`, a PEM block, an assignment
  to a key whose name says secret (`password=`, `client_secret:`, `DEPLOY_TOKEN=`), or an assignment to
  a neutral key (`key`, `token`, `credential`) whose value is itself secret-shaped.
- It does **not** catch a secret that matches no rule. A bare high-entropy value, a base64 blob with
  no label, an unusual vendor token format, or a password in prose all pass through untouched.

So the correct claim is "recognisable secrets are gated, and the gate never silently fails open" — not
"sensitive material never reaches storage". BR-03's business decision about whether such material may
be stored at all is a separate, still-open product question; this is the mechanical half of it, and the
mechanical half is bounded by the rule set above. Widening the rules is the lever if the gap matters;
that is a deliberate, separate decision rather than something this module does implicitly.
"""

import argparse
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
    return not value.startswith("<redacted")


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
RULES = [
    (
        "aws-access-key-id",
        re.compile(r"\b(AKIA[0-9A-Z]{16})\b"),
        0, "<redacted-aws-access-key>", None,
    ),
    (
        "github-token",
        re.compile(r"\b(gh[pousr]_[A-Za-z0-9]{36,255})\b"),
        0, "<redacted-github-token>", None,
    ),
    (
        "private-key-pem",
        re.compile(
            r"-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----.*?"
            r"-----END (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----",
            re.DOTALL,
        ),
        0, "<redacted-private-key>", None,
    ),
    (
        "connection-string-password",
        re.compile(r"(?i)(?:password|pwd)\s*=\s*((['\"]?)[^'\";\s&]+\2)"),
        1, PLACEHOLDER, _not_already_redacted,
    ),
    (
        "aws-secret-access-key",
        re.compile(r"(?i)\baws_secret_access_key\s*=\s*((['\"]?)\S+\2)"),
        1, PLACEHOLDER, None,
    ),
    (
        # A key whose NAME says it holds a secret. Any value of eight or more characters is taken:
        # the name is the evidence, and a false positive here costs a placeholder, not a leak.
        "generic-secret-assignment",
        re.compile(
            r"(?i)(?<![A-Za-z0-9_.-])"
            r"[A-Za-z0-9_.-]*(?:secret|passwd|password|api[_-]?key|access[_-]?key|private[_-]?key"
            r"|[A-Za-z0-9][_.-]*token)"
            r"\s*[:=]\s*"
            r"((['\"]?)[A-Za-z0-9._/+@!#$%&*?~-]{8,}\2)"
        ),
        1, PLACEHOLDER, _not_already_redacted,
    ),
    (
        # A key whose name does NOT say secret — bare `key`/`token`, `sort_key`, `credential` — is
        # redacted only when the value itself is secret-shaped. This is the rule that used to turn
        # `sort key = created_on` into `sort key = <redacted>`.
        "generic-secret-assignment",
        re.compile(
            r"(?i)(?<![A-Za-z0-9_.-])"
            r"(?:[A-Za-z0-9_.-]*key|token|credentials?)"
            r"\s*[:=]\s*"
            r"((['\"]?)[A-Za-z0-9._/+@!#$%&*?~=-]{16,}\2)"
        ),
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
    taken = []  # (start, end, rule_name, replace_start, replace_end, replacement) over the original
    for name, pattern, group, placeholder, accept in RULES:
        pos = 0
        while pos <= len(content):
            match = pattern.search(content, pos)
            if match is None:
                break
            start, end = match.span()
            value_start, value_end = match.span(group)
            value = content[value_start:value_end]
            overlaps = any(start < t_end and t_start < end for t_start, t_end, *_ in taken)
            if overlaps or (accept is not None and not accept(value)):
                pos = start + 1
                continue
            taken.append((start, end, name, value_start, value_end, placeholder))
            pos = end if end > start else end + 1
    taken.sort()
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


def scrub_set_payload(payload):
    """Return (scrubbed_payload, hits) for a `set` request body.

    Returns a **new** payload built from new containers; the input and every object reachable
    from it are left untouched, so a caller holding the original — a dry-run comparison, a
    retry, a test asserting what it planted — still has the value it passed in. Rebuilding is
    what makes that true: mutating the items in place would scrub the caller's dict too, because
    a shallow copy shares them.

    `hits` is a list of {field, rule_name, start, end} — `field` is the JSON path of the altered
    string (`items[0].statement`), `start`/`end` the replaced characters' offsets in it. `digest()`
    turns it into what the write path reports. The matched text is never retained, logged or
    returned.

    A non-dict payload is returned unchanged with no hits. Every caller validates shape
    first and refuses a bad payload on its own terms, so this never has to decide.
    """
    hits = []
    if not isinstance(payload, dict):
        return payload, hits

    def item(source, path):
        if not isinstance(source, dict):
            return source
        clean = {key: _scrub_text(value, f"{path}.{key}", hits) if key in SET_TEXT_FIELDS else value
                 for key, value in source.items()}
        for field in SET_LIST_FIELDS:
            values = clean.get(field)
            if isinstance(values, list):
                clean[field] = [_scrub_text(value, f"{path}.{field}[{index}]", hits)
                                for index, value in enumerate(values)]
        sources = clean.get("sources")
        if isinstance(sources, list):
            clean["sources"] = [
                {key: _scrub_text(value, f"{path}.sources[{index}].{key}", hits)
                 if key in SET_SOURCE_FIELDS else value
                 for key, value in entry.items()}
                if isinstance(entry, dict) else entry
                for index, entry in enumerate(sources)
            ]
        return clean

    def link(source, path):
        if not isinstance(source, dict):
            return source
        return {key: _scrub_text(value, f"{path}.{key}", hits) if key == "reason" else value
                for key, value in source.items()}

    scrubbed = dict(payload)
    if isinstance(payload.get("items"), list):
        scrubbed["items"] = [item(entry, f"items[{index}]")
                             for index, entry in enumerate(payload["items"])]
    if isinstance(payload.get("links"), list):
        scrubbed["links"] = [link(entry, f"links[{index}]")
                             for index, entry in enumerate(payload["links"])]
    if isinstance(payload.get("labelsProposed"), list):
        scrubbed["labelsProposed"] = [_scrub_text(entry, f"labelsProposed[{index}]", hits)
                                      for index, entry in enumerate(payload["labelsProposed"])]
    return scrubbed, hits


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
