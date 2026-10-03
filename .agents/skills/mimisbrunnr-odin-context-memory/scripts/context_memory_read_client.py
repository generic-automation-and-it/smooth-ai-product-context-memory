#!/usr/bin/env python3
"""Read-only CLI surface for delegated context-memory retrieval."""

import argparse
import json
import os
import sys

import context_memory_client as client
import deepsearch

# The read surface, declared once so the framing contract is inspectable rather than implied by which
# subparsers happen to exist. `READ_COMMANDS` is every subcommand this client offers; a name added here
# and not placed in one of the two sets below is a test failure, not a silently unframed surface.
READ_COMMANDS = frozenset({
    "probe", "query", "deepsearch", "get-versions", "get-blob", "paths", "ticket-paths", "labels",
    "initiatives",
})

# The opt-out, with its reason recorded. Each entry must carry no recalled content, or it is a surface
# handing an agent unframed memory. `probe` reports whether a TCP connection succeeds; it returns no
# record, no statement and no body.
#
# Framed is the *default* and is computed as "everything not opted out" — deliberately not a second
# hand-maintained list. Two lists drift: a subcommand added to one and not the other is either silently
# unframed or silently double-declared, and both look fine. Deriving it means the only question to ask
# is whether a name belongs in the opt-out, which is a judgement about content rather than bookkeeping.
UNFRAMED_COMMANDS = frozenset({"probe"})
FRAMED_COMMANDS = frozenset(READ_COMMANDS - UNFRAMED_COMMANDS)

# Commands whose stdout is a body, not a structured result. They are still framed — the banner is the
# only framing a raw body can carry — but the body is never parsed and re-serialised, so a blob that
# happens to be valid JSON comes back byte-identical to what the store holds.
RAW_BODY_COMMANDS = frozenset({"get-blob"})


def main():
    # The read token and base URL were seeded from the machine credential file when this module was
    # imported, so there is nothing to load here. Loading again would be a no-op in every normal path
    # and would restore a deliberately cleared token in the only path where it acts, so a caller could
    # never establish that this surface fails closed without a credential - which is the guarantee the
    # check below exists to support.
    if os.environ.get(client.ENV_WRITE_TOKEN):
        print(f"{client.ENV_WRITE_TOKEN} must not be present in the read worker environment", file=sys.stderr)
        return 2
    parser = argparse.ArgumentParser(prog="context_memory_read_client")
    parser.add_argument("--base-url", help="override " + client.ENV_BASE_URL)
    sub = parser.add_subparsers(dest="command", required=True)

    command = sub.add_parser("probe")
    command.set_defaults(func=client.cmd_probe)
    for name, function in (("query", client.cmd_query), ("paths", client.cmd_paths),
                           ("ticket-paths", client.cmd_ticket_paths)):
        command = sub.add_parser(name)
        command.add_argument("--payload")
        command.set_defaults(func=function)
    command = sub.add_parser("get-versions")
    command.add_argument("uuid")
    command.add_argument("--scope")
    command.set_defaults(func=client.cmd_get_versions)
    command = sub.add_parser("get-blob")
    command.add_argument("uuid")
    command.add_argument("version", type=int)
    command.add_argument("--scope")
    command.set_defaults(func=client.cmd_get_blob)
    command = sub.add_parser("labels")
    command.set_defaults(func=client.cmd_labels)
    command = sub.add_parser("initiatives")
    command.add_argument("--status")
    command.set_defaults(func=client.cmd_initiatives)
    command = sub.add_parser("deepsearch")
    command.add_argument("--payload")
    command.set_defaults(func=lambda args: print_deepsearch(args))

    args = parser.parse_args()
    if args.base_url:
        os.environ[client.ENV_BASE_URL] = args.base_url
    try:
        if args.command in UNFRAMED_COMMANDS:
            args.func(args)
        else:
            _run_framed(args)
    except client.ClientError as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


def _run_framed(args):
    """Run a subcommand and frame whatever it printed.

    Capturing stdout rather than routing each `cmd_*` through `print_recall` is what makes the framing
    a default rather than a per-subcommand decision: nine call sites each remembering to print a notice
    is nine chances to add a tenth and forget, and the read client's own subcommand list is the only place
    that knows which surfaces exist.
    """
    import io
    from contextlib import redirect_stdout

    buffer = io.StringIO()
    with redirect_stdout(buffer):
        result = args.func(args)
    raw = buffer.getvalue().strip()
    if not raw:
        return result
    # Single emission comes from this buffer, not from a banner check: whatever the inner layer printed
    # is captured here and discarded, and only the re-emitted payload reaches stdout. So an inner
    # `cmd_query` banner never gets out, and `print_recall` is free to print its own on every path
    # without coordinating with it.
    payload = _parse_framed_json(raw)
    # Two cases take the banner-and-passthrough path rather than the framed envelope, and they are the
    # same shape: there is nothing to carry a field.
    #
    #  - Not JSON: a bare blob body, or a formatted error.
    #  - A raw-body command whose output happens to parse as JSON. A `get-blob` body that is valid JSON
    #    would otherwise be re-indented and merged into an envelope: the caller asked for a body, not a
    #    parsed object, and re-serialising changes bytes it may be hashing or diffing.
    if payload is _NOT_JSON or getattr(args, "command", None) in RAW_BODY_COMMANDS:
        # The notice check is here only so output that already arrived framed (an inner layer that
        # printed its own banner) is not given a second one; a repeated notice reads as emphasis and
        # trains a reader to scroll past it.
        if client.RECALL_NOTICE not in raw:
            print(client.BANNER_PREFIX + client.RECALL_NOTICE)
        print(raw)
        return result
    client.print_recall(payload)
    return result


# Sentinel distinguishing "not JSON" from "JSON that happens to be null", which a bare `None` cannot do.
_NOT_JSON = object()


def _parse_framed_json(raw: str):
    """Parse stdout that may already carry a banner from an inner framing layer.

    `query` and `deepsearch` are reachable from both the capture client and the read client, and the
    inner one frames on its own. So the text arriving here can be `banner + JSON`, and parsing the whole
    thing as JSON fails — which previously fell through to the not-JSON branch and emitted a *second*
    banner, which reads as emphasis and trains a reader to scroll past it.

    Tried on the whole text first, then from the first brace. The second attempt is what recovers the
    already-bannered case; it cannot misclassify a raw body, because a body that parses from its first
    brace would have parsed whole.
    """
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        pass
    start = raw.find("{")
    if start == -1:
        return _NOT_JSON
    try:
        return json.loads(raw[start:])
    except json.JSONDecodeError:
        return _NOT_JSON


def print_deepsearch(args):
    result = deepsearch.execute(client.read_payload(args.payload))
    print(json.dumps(result, indent=2))
    return result


if __name__ == "__main__":
    raise SystemExit(main())
