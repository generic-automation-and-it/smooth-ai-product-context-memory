#!/usr/bin/env python3
"""Fingerprint secret-redaction detector for the mimisbrunnr-context-memory skill.

Reads a batch of candidate content strings on stdin (JSON array), scrubs the recognised secret spans,
and emits the redacted batch plus a findings list on stdout. The findings report rule NAMES only
(decision 5) — never the matched span, never the content. Content flows via stdin/stdout so it never
appears in argv (visible to ps/logs). Redact-and-flag (LADR-003): a leak is scrubbed and recorded,
never a reason to reject the fact.

Favours false positives: an NFR or rule is worth flagging; a leaked secret that slips through is not.
"""

import argparse
import json
import re
import sys

# Order matters: more specific patterns first so a span is consumed by its own rule.
# Each rule is (rule-name, compiled-regex, replacement). A replacement that keeps group 1 only does so
# when group 1 is a NON-secret label (e.g. "password="); where group 1 is the secret value itself the
# whole match is replaced. Never preserve a secret value in the output.
RULES = [
    (
        "aws-access-key-id",
        re.compile(r"\b(AKIA[0-9A-Z]{16})\b"),
        "<redacted-aws-access-key>",
    ),
    (
        "github-token",
        re.compile(r"\b(gh[pousr]_[A-Za-z0-9]{36,255})\b"),
        "<redacted-github-token>",
    ),
    (
        "private-key-pem",
        re.compile(
            r"-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----.*?"
            r"-----END (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----",
            re.DOTALL,
        ),
        "<redacted-private-key>",
    ),
    (
        "connection-string-password",
        re.compile(
            r"(?i)((?:password|pwd)\s*=\s*)(['\"]?)([^'\";\s&]+)\2",
        ),
        r"\1<redacted>",
    ),
    (
        "aws-secret-access-key",
        re.compile(r"(?i)\b(aws_secret_access_key\s*=\s*)(['\"]?)\S+\2"),
        r"\1<redacted>",
    ),
    (
        "generic-secret-assignment",
        re.compile(
            r"(?i)(?<![A-Za-z0-9])((?:secret|token|api[_-]?key|passwd|password|key)\s*[:=]\s*)(['\"]?)[A-Za-z0-9._/+@!#$%&*?~-]{8,}\2"
        ),
        r"\1<redacted>",
    ),
]

PLACEHOLDER = "<redacted>"

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


def _scrub(content):
    """Return (redacted_content, findings). findings is a dict {rule_name: hit_count}."""
    findings = {}
    for name, pattern, replacement in RULES:
        def _repl(match, replacement=replacement, name=name):
            r = match.expand(replacement)
            findings[name] = findings.get(name, 0) + 1
            return r

        content = pattern.sub(_repl, content)

    return content, findings


def _merge(total, findings):
    for name, count in findings.items():
        total[name] = total.get(name, 0) + count
    return total


def _scrub_text(value, findings):
    """Scrub one string in place of the caller's copy; accumulate its rule counts."""
    if not isinstance(value, str):
        return value
    clean, hit = _scrub(value)
    _merge(findings, hit)
    return clean


def scrub_set_payload(payload):
    """Return (scrubbed_payload, findings) for a `set` request body.

    Returns a **new** payload built from new containers; the input and every object reachable
    from it are left untouched, so a caller holding the original — a dry-run comparison, a
    retry, a test asserting what it planted — still has the value it passed in. Rebuilding is
    what makes that true: mutating the items in place would scrub the caller's dict too, because
    a shallow copy shares them.

    `findings` is {rule_name: hit_count} across the whole batch, which is the digest the write
    path reports. Counts are the only signal carried: the matched span is never retained, logged
    or returned, and a rule name says nothing about the value it replaced.

    A non-dict payload is returned unchanged with no findings. Every caller validates shape
    first and refuses a bad payload on its own terms, so this never has to decide.
    """
    findings = {}
    if not isinstance(payload, dict):
        return payload, findings

    def item(source):
        if not isinstance(source, dict):
            return source
        clean = {key: _scrub_text(value, findings) if key in SET_TEXT_FIELDS else value
                 for key, value in source.items()}
        for field in SET_LIST_FIELDS:
            values = clean.get(field)
            if isinstance(values, list):
                clean[field] = [_scrub_text(value, findings) for value in values]
        sources = clean.get("sources")
        if isinstance(sources, list):
            clean["sources"] = [
                {key: _scrub_text(value, findings) if key in SET_SOURCE_FIELDS else value
                 for key, value in source.items()}
                if isinstance(source, dict) else source
                for source in sources
            ]
        return clean

    def link(source):
        if not isinstance(source, dict):
            return source
        return {key: _scrub_text(value, findings) if key == "reason" else value
                for key, value in source.items()}

    scrubbed = dict(payload)
    if isinstance(payload.get("items"), list):
        scrubbed["items"] = [item(entry) for entry in payload["items"]]
    if isinstance(payload.get("links"), list):
        scrubbed["links"] = [link(entry) for entry in payload["links"]]
    if isinstance(payload.get("labelsProposed"), list):
        scrubbed["labelsProposed"] = [_scrub_text(entry, findings)
                                      for entry in payload["labelsProposed"]]
    return scrubbed, findings


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
        redacted, findings = _scrub(content)
        results.append(
            {
                "candidate_index": index,
                "redacted": redacted,
                "findings": [{"rule_name": name, "hit_count": count} for name, count in sorted(findings.items())],
            }
        )

    print(json.dumps({"results": results}, indent=2))


if __name__ == "__main__":
    main()
