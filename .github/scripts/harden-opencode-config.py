#!/usr/bin/env python3
"""Apply the consumer's secret-file fence to a resolved upstream OpenCode config."""

import json
import os
import sys
import tempfile
from pathlib import Path


DENIED_PATHS = {
    "*.git": "deny",
    "*.git/*": "deny",
    "*.env": "deny",
    "*.env.*": "deny",
    "*.config/gh/*": "deny",
    # A process's environment is readable at /proc/<pid>/environ — and at one more level of
    # nesting per thread, /proc/<pid>/task/<tid>/environ, which is a *different file* with the
    # same contents. A single-depth glob admits the thread form, so both are denied. `/proc/self`
    # is a symlink to a pid directory and resolves through either depth.
    "*/proc/*/environ": "deny",
    "*/proc/*/cmdline": "deny",
    "*/proc/*/task/*/environ": "deny",
    "*/proc/*/task/*/cmdline": "deny",
}


def fence(value):
    if value == "deny":
        return value
    rules = dict(value) if isinstance(value, dict) else {"*": "allow"}
    rules.update(DENIED_PATHS)
    rules["*.env.example"] = "allow"
    return rules


def main(path):
    source = Path(path)
    data = json.loads(source.read_text(encoding="utf-8"))
    legacy = data.get("agent")
    native = data.get("agents")
    if not isinstance(legacy, dict) and not isinstance(native, dict):
        raise ValueError("OpenCode config has no review/analyse agent definitions")
    for name in ("review", "analyse"):
        use_native = isinstance(native, dict) and isinstance(native.get(name), dict)
        agent = native[name] if use_native else (legacy or {}).get(name)
        if agent is None:
            raise ValueError(f"OpenCode config is missing the {name} agent")
        if use_native:
            permissions = agent.setdefault("permissions", [])
            if not isinstance(permissions, list):
                raise ValueError(f"{name} permissions must be a list")
            for action in (("read", "edit") if name == "analyse" else ("read",)):
                permissions.extend(
                    {"action": action, "resource": pattern, "effect": "deny"}
                    for pattern in DENIED_PATHS
                )
            permissions.append({"action": "grep", "resource": "*", "effect": "deny"})
            if name == "analyse":
                permissions.append(
                    {"action": "external_directory", "resource": "*", "effect": "deny"}
                )
            continue
        permissions = agent.setdefault("permission", {})
        tools = agent.setdefault("tools", {})
        permissions["read"] = fence(permissions.get("read", "allow"))
        if name == "analyse":
            permissions["edit"] = fence(permissions.get("edit", "allow"))
            permissions["external_directory"] = "deny"
        permissions["grep"] = "deny"  # OpenCode matches grep rules on the query, not the file path.
        tools["grep"] = False

    fd, temporary = tempfile.mkstemp(prefix=".opencode-guard-", dir=source.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(data, stream, indent=2)
            stream.write("\n")
        os.chmod(temporary, source.stat().st_mode & 0o777)
        os.replace(temporary, source)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: harden-opencode-config.py CONFIG")
    main(sys.argv[1])
