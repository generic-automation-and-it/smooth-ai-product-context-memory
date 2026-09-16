#!/usr/bin/env python3
"""Main-only release identity and fail-closed promotion policy. No remote writes."""

import json
import os
import re
import subprocess
import sys


def identity(env):
    if env["GITHUB_EVENT_NAME"] != "push" or env["GITHUB_REF"] != "refs/heads/main":
        raise ValueError("Publication is allowed only for pushes to main")
    revision = env["GITHUB_SHA"]
    run_id = env["GITHUB_RUN_ID"]
    attempt = env["GITHUB_RUN_ATTEMPT"]
    if not re.fullmatch(r"[0-9a-f]{40}", revision) or not all(
        re.fullmatch(r"[1-9][0-9]*", value) for value in (run_id, attempt)
    ):
        raise ValueError("Invalid source revision or run identity")
    candidate = f"candidate-{revision}-{run_id}-{attempt}"
    if len(candidate) > 128:
        raise ValueError("Candidate exceeds the OCI tag limit")
    return {
        "aliases": ["latest", f"sha-{revision[:7]}"],
        "candidate": candidate,
        "release_version": f"main-{revision[:7]}",
    }


def remote_refs():
    result = subprocess.run(
        ["git", "ls-remote", "origin", "refs/heads/main"],
        check=True, capture_output=True, text=True,
    )
    return dict((ref, sha) for sha, ref in (line.split() for line in result.stdout.splitlines()))


def promotion_aliases(release, revision, refs):
    if "refs/heads/main" not in refs:
        raise ValueError("Cannot verify current main revision")
    aliases = release["aliases"].copy()
    if refs["refs/heads/main"] != revision:
        aliases.remove("latest")
    return aliases


def main():
    release = identity(os.environ)
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command == "prepare":
        values = release
    elif command == "aliases":
        values = {"aliases": promotion_aliases(release, os.environ["GITHUB_SHA"], remote_refs())}
    else:
        raise ValueError("Unknown policy command")
    for key, value in values.items():
        print(f"{key}={value if isinstance(value, str) else json.dumps(value, separators=(',', ':'))}")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
