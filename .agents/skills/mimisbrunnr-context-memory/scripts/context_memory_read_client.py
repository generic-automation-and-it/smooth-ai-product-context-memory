#!/usr/bin/env python3
"""Read-only CLI surface for delegated context-memory retrieval."""

import argparse
import os
import sys

import context_memory_client as client
import deepsearch


def main():
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
        args.func(args)
    except client.ClientError as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


def print_deepsearch(args):
    import json

    result = deepsearch.execute(client.read_payload(args.payload))
    print(json.dumps(result, indent=2))
    return result


if __name__ == "__main__":
    raise SystemExit(main())
