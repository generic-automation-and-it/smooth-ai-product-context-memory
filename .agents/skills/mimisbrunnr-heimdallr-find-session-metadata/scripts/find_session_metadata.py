#!/usr/bin/env python3
"""Heimdallr session-metadata scan: tickets, repository, initiative -> console.

Offline and read-only. Reads only git output (remote, branch, recent subjects);
never opens .context/, .env* or *.env, never touches the network or the store.
Initiative comes from --initiative only and is otherwise "unknown".
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys

REFUSED_DIRS = (".context/", ".env", ".env.", ".envrc")
# Branch names carry the number as a path segment (feat/160-...), not a # shorthand.
BRANCH_TICKET = re.compile(r"/(\d{1,7})(?=[-/]|$)")
TICKET_PATTERNS = (
    # #123 -> github:123 (convention shared with the understanding export)
    re.compile(r"#(\d{1,7})\b"),
    # provider:key, e.g. github:160, jira:ABC-123, linear:XYZ-42
    re.compile(r"\b([a-zA-Z][a-zA-Z0-9_-]{1,30}):([A-Za-z0-9_.-]{1,60})\b"),
    # JIRA-style bare key, e.g. ABC-123
    re.compile(r"\b([A-Z][A-Z0-9]{1,9}-\d{1,7})\b"),
)


def _git(*argv: str) -> str | None:
    try:
        proc = subprocess.run(
            ["git", *argv], capture_output=True, text=True, encoding="utf-8"
        )
    except (FileNotFoundError, OSError):
        return None
    if proc.returncode != 0:
        return None
    return proc.stdout.strip()


def parse_repo(url: str) -> str | None:
    """owner/repo from a git remote URL, or None when it is not provable."""
    text = (url or "").strip()
    text = re.sub(r"^(?:ssh://git@|git@)", "", text)
    text = re.sub(r"^https?://[^/]+/", "", text)
    text = text.replace(":", "/", 1) if ":" in text.split("/")[0] else text
    match = re.search(r"([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?$", text)
    if not match:
        return None
    repo = match.group(1)
    return repo if "/" in repo and " " not in repo else None


def find_tickets(*texts: str) -> list[dict]:
    """Deduped tickets in first-seen order, each with its source label."""
    seen: dict[str, dict] = {}
    for label, text in texts:
        if label == "branch":
            for match in BRANCH_TICKET.finditer(text or ""):
                ident = "github:%s" % match.group(1)
                if ident not in seen:
                    seen[ident] = {
                        "provider": "github",
                        "key": match.group(1),
                        "seenIn": label,
                    }
        for pattern in TICKET_PATTERNS:
            for match in pattern.finditer(text or ""):
                if match.re.pattern.startswith("#"):
                    provider, key = "github", match.group(1)
                elif ":" in match.group(0) and match.lastindex == 2:
                    provider, key = match.group(1).lower(), match.group(2)
                else:
                    provider, key = "local", match.group(1)
                ident = "%s:%s" % (provider, key)
                if ident not in seen:
                    seen[ident] = {"provider": provider, "key": key, "seenIn": label}
    return list(seen.values())


def scan(initiative: str | None = None) -> dict:
    remote = _git("remote", "get-url", "origin")
    branch = _git("branch", "--show-current")
    # Pin the window to the branch's own ref (HEAD when detached): an implicit-HEAD `git log` reads
    # whichever tip the checkout sits on, so the same branch state must always yield the same subjects.
    ref = branch if branch else "HEAD"
    log = _git("log", ref, "--format=%s", "-n", "10")
    subjects = (log or "").splitlines()

    texts = []
    if branch:
        texts.append(("branch", branch))
    for subject in subjects:
        texts.append(("commit", subject))
    tickets = find_tickets(*texts)

    return {
        "repository": parse_repo(remote or ""),
        "repositorySource": "git remote get-url origin" if remote else None,
        "tickets": tickets,
        "initiative": initiative or "unknown",
        "initiativeSource": "--initiative flag" if initiative else None,
        "branch": branch,
    }


def render_human(result: dict) -> str:
    lines = ["Session metadata (offline git scan, nothing written):"]
    lines.append("- repository: %s" % (result["repository"] or "unknown"))
    if result["tickets"]:
        lines.append("- tickets:")
        for ticket in result["tickets"]:
            lines.append(
                "  - %s:%s (seen in %s)"
                % (ticket["provider"], ticket["key"], ticket["seenIn"])
            )
    else:
        lines.append("- tickets: none found")
    lines.append("- initiative: %s" % result["initiative"])
    lines.append("- branch: %s" % (result["branch"] or "unknown"))
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true", help="JSON only to stdout")
    parser.add_argument("--initiative", default=None, help="initiative name (only source)")
    args = parser.parse_args(argv)
    result = scan(initiative=args.initiative)
    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))
    else:
        print(render_human(result), end="")
    return 0


if __name__ == "__main__":
    sys.exit(main())
