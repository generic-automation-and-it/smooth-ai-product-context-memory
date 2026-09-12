#!/usr/bin/env python3
"""Fingerprint secret-redaction detector for the context-memory skill.

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
        re.compile(r"(?i)\b(aws_secret_access_key\s*=\s*)(?:(['\"])(?:(?!\2).)+\2|\S+)"),
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
