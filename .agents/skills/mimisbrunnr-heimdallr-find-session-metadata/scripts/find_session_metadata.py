#!/usr/bin/env python3
"""Heimdallr session-metadata scan: tickets, repository, initiative -> console.

Offline and read-only. Reads only git output (remote, branch, recent subjects);
never opens .context/, .env* or *.env, never touches the network or the store.
Initiative comes from --initiative only and is otherwise "unknown".

Every ticket candidate passes the capture skill's redactor first: a candidate whose provider is a
credential word, or whose text (or the span of the subject it sits in) the redactor would change, is
withheld and only counted — never printed. The branch name is withheld the same way, and when a candidate
inside it was. Without the redactor no ticket and no branch name is reported at all. A failed `git log`
is disclosed as `commitsUnavailable`, never reported as an empty history.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import re
import subprocess
import sys
from pathlib import Path

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


# The capture skill's redactor, found beside this skill the way the other Mímisbrunnr clients find it;
# both script folders ship together in the npm package and the Claude plugin.
REDACTOR = (Path(__file__).resolve().parents[2] / "mimisbrunnr-odin-context-memory" / "scripts"
            / "redact.py")
REDACTOR_UNAVAILABLE = "redactor unavailable; no unchecked ticket is reported"
BRANCH_CREDENTIAL_SHAPED = "credential-shaped; not shown"
BRANCH_UNCHECKED = "redactor unavailable; the branch is not shown unchecked"
COMMITS_UNAVAILABLE = "git log failed; recent commit subjects were not read"
ROOT_CREDENTIAL_SHAPED = "credential-shaped checkout path; not shown"
ROOT_UNCHECKED = "redactor unavailable; the checkout path is not shown unchecked"
REPOSITORY_CREDENTIAL_SHAPED = "credential-shaped origin path; not shown"
REPOSITORY_UNCHECKED = "redactor unavailable; the repository is not shown unchecked"


def _shown_root(redactor, root: str | None) -> tuple[str | None, str | None]:
    """The checkout path as it may be printed, and why it is withheld when it may not (issue 188).

    An absolute checkout path starts with the account's home folder — `/Users/<name>/…` — so printing
    it put the operator's account name, and anything credential-shaped further down the path, into
    every transcript: the branch-name class of issues 184 and 186, one field over. The home prefix is
    shown as `~`, and the rest is withheld when the redactor would change it, a segment is a credential
    word, or — failing closed — no redactor can check it. `rootMatches` carries the comparison a caller
    actually needs, so nothing has to compare paths against this display form.
    """
    if not root:
        return None, None
    if redactor is None:
        return None, ROOT_UNCHECKED
    home = os.path.realpath(os.path.expanduser("~"))
    real = os.path.realpath(root)
    shown = "~" + real[len(home):] if real == home or real.startswith(home + os.sep) else real
    if redactor.scrub_located(shown)[0] != shown or any(
            redactor.is_credential_key(segment) for segment in shown.split("/")):
        return None, ROOT_CREDENTIAL_SHAPED
    return shown, None


def _root_matches(root: str | None, repo_root: str | None) -> bool | None:
    """Whether the scanned checkout's top level is the requested root; None when none was requested."""
    if repo_root is None:
        return None
    return bool(root) and os.path.realpath(root) == os.path.realpath(repo_root)


def _repository_withheld(redactor, repository: str | None) -> str | None:
    """Why the parsed origin path must not be printed, or None when it may be (issue 186).

    `parse_repo` drops the host and any userinfo, but every path segment survives, so an origin such as
    `https://host/x-access-token/<token>/org/repo.git` printed the token as part of the repository — the
    same class as the branch name, one field over. The path is withheld when the redactor would change
    it or a segment is a credential word, and, failing closed like the tickets, when no redactor can
    check it.
    """
    if not repository:
        return None
    if redactor is None:
        return REPOSITORY_UNCHECKED
    if redactor.scrub_located(repository)[0] != repository or any(
            redactor.is_credential_key(segment) for segment in repository.split("/")):
        return REPOSITORY_CREDENTIAL_SHAPED
    return None


def load_redactor():
    """The redactor module, or None when it cannot be loaded or lacks the two calls used here.

    Loaded from its file rather than by `import redact`, so a same-named module elsewhere on the path
    can never stand in for it.
    """
    try:
        spec = importlib.util.spec_from_file_location("_heimdallr_redact", REDACTOR)
        if spec is None or spec.loader is None:
            return None
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
    except Exception:  # noqa: BLE001 — any failure to load means no checked ticket can be reported
        return None
    if not callable(getattr(module, "is_credential_key", None)) \
            or not callable(getattr(module, "scrub_located", None)):
        return None
    return module


class GitUnavailable(RuntimeError):
    """git could not determine session metadata (not a repo, or git is not on PATH).

    Raised rather than returning an empty result so a caller can tell "no tickets in this repo"
    from "git cannot run here" — the consumer treats the non-zero exit as autofill unavailable
    instead of a genuine empty recall.
    """


def _git(*argv: str, root: str | None = None) -> str | None:
    # `-C` only when a root is named, so the default scan is the process's own checkout.
    prefix = ["git", "-C", root] if root else ["git"]
    try:
        proc = subprocess.run(
            [*prefix, *argv], capture_output=True, text=True, encoding="utf-8"
        )
    except (FileNotFoundError, OSError):
        raise GitUnavailable("git is not on PATH or could not be run") from None
    if proc.returncode != 0:
        return None
    return proc.stdout.strip()


def parse_repo(url: str) -> str | None:
    """The repo path (all segments) from a git remote URL, or None when unprovable."""
    text = (url or "").strip()
    if re.match(r"^https?://", text):
        text = re.sub(r"^https?://[^/]+/", "", text)
    elif re.match(r"^(?:ssh://)?git@", text):
        # scp-style `git@host:owner/.../repo` or `ssh://git@host[:port]/owner/.../repo`; drop the
        # host (and any SSH port, which a self-hosted tracker may set).
        text = re.sub(r"^(?:ssh://)?git@[^:/]+(?::\d+)?[:/]", "", text)
    else:
        return None
    # Drop any query/fragment and a trailing `.git` (with an optional trailing slash some remotes
    # append), so `owner/repo.git?ref=x` and `owner/repo.git/` still resolve to `owner/repo`.
    text = re.sub(r"[?#].*$", "", text)
    text = re.sub(r"\.git/?$", "", text)
    # Capture every slash-separated segment, not just the last pair: a GitLab-style remote
    # `group/subgroup/repo.git` is one repository, and the last-pair form silently returned
    # `subgroup/repo`. The `+` after the first segment guarantees at least owner/repo.
    match = re.search(r"([A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)+)$", text)
    if not match:
        return None
    repo = match.group(1)
    return repo if "/" in repo and " " not in repo else None


def _credential_shaped(redactor, provider: str, candidate: str, start: int, end: int,
                       hits: list) -> bool:
    """A ticket-shaped candidate that is really a credential (issue 182).

    `provider:key` matches any `word:value`, so `password:…` and `token:ghp_…` in a commit subject read
    as tickets and were printed — into the agent transcript, and through kvasir's autofill into a store
    binding. Any provider is accepted except a credential word, and a candidate is refused when the
    redactor would change it alone or when it overlaps a span the redactor finds in its whole subject
    (`password=ABC-1234` names a bare key the redactor sees only in context).
    """
    if redactor.is_credential_key(provider):
        return True
    if redactor.scrub_located(candidate)[0] != candidate:
        return True
    return any(s < end and start < e for _rule, s, e in hits)


def find_tickets(*texts: str, redactor=None, withheld: list | None = None) -> list[dict]:
    """Deduped tickets in first-seen order, each with its source label.

    With a `redactor`, credential-shaped candidates are dropped and counted in `withheld` (one entry
    per dropped occurrence, carrying no value).
    """
    seen: dict[str, dict] = {}
    for label, text in texts:
        text = text or ""
        # (offset, pattern rank, provider, key): sorting on offset reports a subject's tickets in the
        # order they appear, not in pattern order — `fix ABC-123 and #456` must list ABC-123 first.
        found = []
        if label == "branch":
            for match in BRANCH_TICKET.finditer(text):
                found.append((match.start(), 0, "github", match.group(1), match.end()))
        # The bare-key pattern matches ABC-123 inside `jira:ABC-123`, which would re-emit it as a
        # spurious `local:ABC-123`. Collect the provider:key spans first and suppress bare-key
        # matches that fall within one — the provider:key already captured that ticket.
        provider_spans = [m.span() for m in TICKET_PATTERNS[1].finditer(text)]
        for rank, pattern in enumerate(TICKET_PATTERNS, start=1):
            for match in pattern.finditer(text):
                if pattern is TICKET_PATTERNS[2] and any(
                        match.start() >= s and match.end() <= e for (s, e) in provider_spans):
                    continue
                if pattern is TICKET_PATTERNS[0]:
                    provider, key = "github", match.group(1)
                elif pattern is TICKET_PATTERNS[1]:
                    provider, key = match.group(1).lower(), match.group(2)
                else:
                    provider, key = "local", match.group(1)
                found.append((match.start(), rank, provider, key, match.end()))
        hits = redactor.scrub_located(text)[1] if redactor is not None else []
        for offset, _rank, provider, key, end in sorted(found):
            if redactor is not None and _credential_shaped(
                    redactor, provider, text[offset:end], offset, end, hits):
                if withheld is not None:
                    withheld.append(label)
                continue
            ident = "%s:%s" % (provider, key)
            if ident not in seen:
                seen[ident] = {"provider": provider, "key": key, "seenIn": label}
    return list(seen.values())


def _unborn(branch: str | None, repo_root: str | None) -> bool:
    """True only on positive evidence that `branch` has no commit yet (a fresh `git init`)."""
    if not branch:
        return False  # a detached HEAD always names a commit
    head = _git("symbolic-ref", "-q", "HEAD", root=repo_root)
    if head != f"refs/heads/{branch}":
        return False  # git could not read HEAD, or it points elsewhere: not evidence of anything
    return _git("show-ref", "--verify", "-q", f"refs/heads/{branch}", root=repo_root) is None


def scan(initiative: str | None = None, repo_root: str | None = None) -> dict:
    # Distinguish "no metadata in this repo" from "git cannot run here". A repo with no ticket
    # commits is a legitimate empty `tickets: []`; a non-git checkout or a missing git binary must
    # not masquerade as "no tickets", so the consumer reports autofill unavailable.
    # A `git` inside a work tree answers "true"; any other answer — a non-work-tree ("false", e.g.
    # a bare clone or `.git/`), a non-repo error (None), or a missing git binary (GitUnavailable) —
    # means the autofill cannot run and must not masquerade as "no tickets".
    if _git("rev-parse", "--is-inside-work-tree", root=repo_root) != "true":
        raise GitUnavailable("not a git repository or git is unavailable")
    # The scanned checkout's top level is reported so a caller can prove *which* repository this is:
    # without it, a scan run from another working directory bound that checkout's repo and tickets
    # with nothing in the output to tell them apart (issue 182).
    root = _git("rev-parse", "--show-toplevel", root=repo_root)
    remote = _git("remote", "get-url", "origin", root=repo_root)
    branch = _git("branch", "--show-current", root=repo_root)
    # Pin the window to the branch's own ref (HEAD when detached): an implicit-HEAD `git log` reads
    # whichever tip the checkout sits on, so the same branch state must always yield the same subjects.
    ref = branch if branch else "HEAD"
    log = _git("log", ref, "--format=%s", "-n", "10", root=repo_root)
    commits_unavailable = None
    if log is None and not _unborn(branch, repo_root):
        # A failed `git log` is a read failure unless the branch is provably unborn. Issue 184 inferred
        # "unborn" from a second failed read (the ref did not resolve), so when git itself could not
        # read the repository both reads failed and a broken read still looked like an empty history
        # (issue 188). Unborn now needs positive evidence: HEAD is a readable symbolic ref to this
        # branch and the branch has no ref of its own.
        commits_unavailable = COMMITS_UNAVAILABLE
    subjects = (log or "").splitlines()

    texts = []
    if branch:
        texts.append(("branch", branch))
    for subject in subjects:
        texts.append(("commit", subject))
    redactor = load_redactor()
    withheld: list = []
    if redactor is None:
        # Fail closed: an unchecked ticket can carry a credential, so none is reported.
        tickets, unavailable = [], REDACTOR_UNAVAILABLE
    else:
        tickets, unavailable = find_tickets(*texts, redactor=redactor, withheld=withheld), None
    shown_branch, branch_withheld = branch, None
    if branch is None:
        branch_withheld = "branch read failed; not shown"
    elif branch:
        # The branch name is free text like a commit subject, and it was printed whole while its
        # ticket candidates were checked (issue 184). It is withheld when the redactor would change it
        # or a candidate inside it was withheld — printing it would print that value — and, failing
        # closed like the tickets, when no redactor is available to check it.
        if redactor is None:
            branch_withheld = BRANCH_UNCHECKED
        elif "branch" in withheld or redactor.scrub_located(branch)[0] != branch:
            branch_withheld = BRANCH_CREDENTIAL_SHAPED
        if branch_withheld:
            shown_branch = None

    shown_root, root_withheld = _shown_root(redactor, root)
    repository = parse_repo(remote or "")
    repository_withheld = _repository_withheld(redactor, repository)
    return {
        "repository": None if repository_withheld else repository,
        "repositoryWithheld": repository_withheld,
        "repositorySource": "git remote get-url origin" if remote else None,
        "tickets": tickets,
        "ticketsWithheld": len(withheld),
        "ticketsUnavailable": unavailable,
        "commitsUnavailable": commits_unavailable,
        "initiative": initiative or "unknown",
        "initiativeSource": "--initiative flag" if initiative else None,
        "branch": shown_branch,
        "branchWithheld": branch_withheld,
        "root": shown_root,
        "rootWithheld": root_withheld,
        "rootMatches": _root_matches(root, repo_root),
    }


def render_human(result: dict) -> str:
    lines = ["Session metadata (offline git scan, nothing written):"]
    if result.get("repositoryWithheld"):
        lines.append("- repository: withheld (%s)" % result["repositoryWithheld"])
    else:
        lines.append("- repository: %s" % (result["repository"] or "unknown"))
    if result["tickets"]:
        lines.append("- tickets:")
        for ticket in result["tickets"]:
            lines.append(
                "  - %s:%s (seen in %s)"
                % (ticket["provider"], ticket["key"], ticket["seenIn"])
            )
    elif result.get("ticketsUnavailable"):
        lines.append("- tickets: unavailable (%s)" % result["ticketsUnavailable"])
    else:
        lines.append("- tickets: none found")
    if result.get("commitsUnavailable"):
        # Tickets above come from the branch alone; an empty list is not an empty history.
        lines.append("- commits: unavailable (%s)" % result["commitsUnavailable"])
    if result.get("ticketsWithheld"):
        lines.append("- withheld: %d credential-shaped candidate(s), not shown"
                     % result["ticketsWithheld"])
    lines.append("- initiative: %s" % result["initiative"])
    if result.get("branchWithheld"):
        lines.append("- branch: withheld (%s)" % result["branchWithheld"])
    else:
        lines.append("- branch: %s" % (result["branch"] or "unknown"))
    if result.get("rootWithheld"):
        lines.append("- root: withheld (%s)" % result["rootWithheld"])
    else:
        lines.append("- root: %s" % (result.get("root") or "unknown"))
    if result.get("rootMatches") is not None:
        lines.append("- root matches --repo-root: %s" % ("yes" if result["rootMatches"] else "no"))
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true", help="JSON only to stdout")
    parser.add_argument("--initiative", default=None, help="initiative name (only source)")
    parser.add_argument("--repo-root", default=None, metavar="DIR",
                        help="scan this checkout (git -C DIR) instead of the working directory")
    args = parser.parse_args(argv)
    try:
        result = scan(initiative=args.initiative, repo_root=args.repo_root)
    except GitUnavailable as exc:
        # Exit non-zero (not 0) so the consumer's `heimdallr_scan` treats this as autofill
        # unavailable, not as a genuine empty recall.
        print(f"git unavailable: {exc}", file=sys.stderr)
        return 2
    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))
    else:
        print(render_human(result), end="")
    return 0


if __name__ == "__main__":
    sys.exit(main())
