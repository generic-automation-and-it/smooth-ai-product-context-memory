#!/usr/bin/env python3
"""Release identity and fail-closed promotion policy. No remote writes."""

import json
import os
import re
import subprocess
import sys


def version_parts(version):
    match = re.fullmatch(
        r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
        r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?", version
    )
    if not match or len(version) > 128:
        raise ValueError("Version must be SemVer, at most 128 ASCII characters, without v or build metadata")
    prerelease = match[4] or ""
    if any(part.isdigit() and len(part) > 1 and part[0] == "0" for part in prerelease.split(".")):
        raise ValueError("Numeric prerelease identifiers must not have leading zeros")
    return tuple(int(match[index]) for index in (1, 2, 3)), prerelease


def identity(env):
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
    event, ref = env["GITHUB_EVENT_NAME"], env["GITHUB_REF"]
    version = ""
    if event == "workflow_dispatch":
        version = env.get("INPUT_VERSION", "")
        version_parts(version)
    elif event == "push" and ref.startswith("refs/tags/v"):
        version = ref.removeprefix("refs/tags/v")
        version_parts(version)
    elif event != "push" or ref != "refs/heads/main":
        raise ValueError("Unsupported publication event/ref")

    aliases = [version or "latest", f"sha-{revision[:7]}"]
    prerelease = bool(version and version_parts(version)[1])
    if version and not prerelease and event == "push":
        aliases.insert(1, version.rsplit(".", 1)[0])
    return {
        "aliases": aliases,
        "candidate": candidate,
        "create_release": bool(version),
        "prerelease": prerelease,
        "release_tag": f"v{version}" if version else "",
        "release_version": version or f"main-{revision[:7]}",
    }


def remote_refs():
    result = subprocess.run(
        ["git", "ls-remote", "origin", "refs/heads/main", "refs/tags/*"],
        check=True, capture_output=True, text=True,
    )
    return dict((ref, sha) for sha, ref in (line.split() for line in result.stdout.splitlines()))


def check_target(release, revision, refs):
    tag = release["release_tag"]
    if not tag:
        return False
    ref = f"refs/tags/{tag}"
    target = refs.get(f"{ref}^{{}}", refs.get(ref))
    if target is not None and target != revision:
        raise ValueError("Release tag does not point to the tested commit")
    return target is not None


def promotion_aliases(release, revision, refs):
    if release["create_release"] and not check_target(release, revision, refs):
        raise ValueError("Release tag must exist at the tested commit before promotion")
    aliases = release["aliases"].copy()
    if "latest" in aliases and refs.get("refs/heads/main") != revision:
        aliases.remove("latest")
    version = release["release_version"]
    if release["create_release"] and not release["prerelease"]:
        current, _ = version_parts(version)
        lane = version.rsplit(".", 1)[0]
        for ref in refs:
            if not ref.startswith("refs/tags/v") or ref.endswith("^{}"):
                continue
            try:
                other, prerelease = version_parts(ref.removeprefix("refs/tags/v"))
            except ValueError:
                continue
            # A newer source tag reserves the lane even if its build is still pending.
            if not prerelease and other[:2] == current[:2] and other > current and lane in aliases:
                aliases.remove(lane)
    return aliases


def check_version_images(version, images):
    for image, expected_digest in images:
        result = subprocess.run(
            ["docker", "buildx", "imagetools", "inspect", f"{image}:{version}",
             "--format", "{{.Manifest.Digest}}"],
            capture_output=True, text=True,
        )
        if result.returncode:
            error = result.stderr.lower().strip()
            if error.endswith(": not found") or "manifest unknown" in error:
                continue
            raise ValueError("Cannot verify existing release image; refusing promotion")
        if result.stdout.strip() != expected_digest:
            raise ValueError("Release version already has a different image digest; use a new version")


def main():
    release = identity(os.environ)
    command = sys.argv[1]
    if command == "prepare":
        values = release
    elif command == "check-tag":
        exists = check_target(release, os.environ["GITHUB_SHA"], remote_refs())
        if not exists and os.environ["GITHUB_EVENT_NAME"] != "workflow_dispatch":
            raise ValueError("Push release tag is missing")
        values = {"tag_exists": exists}
    elif command == "aliases":
        values = {"aliases": promotion_aliases(release, os.environ["GITHUB_SHA"], remote_refs())}
        if release["create_release"]:
            check_version_images(release["release_version"], [
                (os.environ["API_IMAGE"], os.environ["API_DIGEST"]),
                (os.environ["APPHOST_IMAGE"], os.environ["APPHOST_DIGEST"]),
            ])
    elif command == "verify-tag":
        if not check_target(release, os.environ["GITHUB_SHA"], remote_refs()):
            raise ValueError("Release tag is missing")
        values = {}
    else:
        raise ValueError("Unknown policy command")
    for key, value in values.items():
        print(f"{key}={value if isinstance(value, str) else json.dumps(value, separators=(',', ':'))}")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
