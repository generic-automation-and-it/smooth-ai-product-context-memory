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
RAW_BODY_COMMANDS = client.RAW_BODY_COMMANDS


def main():
    # The read token and base URL were seeded from the machine credential file when this module was
    # imported, so there is nothing to load here. Loading again would be a no-op in every normal path
    # and would restore a deliberately cleared token in the only path where it acts, so a caller could
    # never establish that this surface fails closed without a credential - which is the guarantee the
    # check below exists to support.
    present = client.write_tokens_present()
    if present:
        print(f"{', '.join(present)} must not be present in the read worker environment "
              f"(a write credential in any spelling, {client.ENV_WRITE_TOKEN} or the Host's "
              f"ApiAccess__WriteToken)", file=sys.stderr)
        return 2
    parser = argparse.ArgumentParser(prog="context_memory_read_client")
    parser.add_argument("--base-url", help="override " + client.ENV_BASE_URL)
    sub = parser.add_subparsers(dest="command", required=True)

    command = sub.add_parser("probe")
    command.set_defaults(func=client.cmd_probe)
    for name, function in (("query", client.cmd_query), ("paths", client.cmd_paths),
                           ("ticket-paths", client.cmd_ticket_paths)):
        command = sub.add_parser(name, aliases=["import"] if name == "query" else [])
        client.add_payload_arguments(command)
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
    client.add_payload_arguments(command)
    command.set_defaults(func=lambda args: print_deepsearch(args))

    args = parser.parse_args()
    args.command = client.COMMAND_ALIASES.get(args.command, args.command)
    if args.base_url:
        os.environ[client.ENV_BASE_URL] = args.base_url
    try:
        if args.command in UNFRAMED_COMMANDS:
            args.func(args)
        else:
            _run_framed(args)
    except (client.ClientError, ValueError) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


# The framing dispatch lives in the shared client module so the capture client's read commands use the
# same code (issue 190); the names are kept here for the read client's own dispatch and its tests.
_run_framed = client.run_framed
_parse_framed_json = client._parse_framed_json
_NOT_JSON = client._NOT_JSON


def print_deepsearch(args):
    result = deepsearch.execute(client.payload_of(args))
    print(json.dumps(result, indent=2))
    return result


if __name__ == "__main__":
    raise SystemExit(main())
