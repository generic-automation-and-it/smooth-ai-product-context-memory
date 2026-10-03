import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


class ClientError(Exception):
    pass


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ClientError("redirect_refused")


def configuration(env=None):
    settings = dict(os.environ if env is None else env)
    filename = settings.get("MIMI_KNOWLEDGE_CREDENTIALS_FILE")
    if filename:
        path = Path(filename)
        if path.is_symlink() or not path.is_file():
            raise ClientError("invalid_credentials_file")
        if os.name != "nt" and path.stat().st_mode & 0o077:
            raise ClientError("credentials_file_requires_mode_600")
        try:
            lines = path.read_text(encoding="utf-8").splitlines()
        except OSError:
            raise ClientError("credentials_file_unreadable") from None
        for line in lines:
            key, separator, value = line.strip().partition("=")
            if separator and key in {"MIMI_KNOWLEDGE_URL", "MIMI_KNOWLEDGE_READ_TOKEN", "MIMI_KNOWLEDGE_WRITE_TOKEN"}:
                settings.setdefault(key, value.strip().strip("'\""))
    return settings


class Client:
    def __init__(self, settings=None, timeout=30):
        self.settings = configuration() if settings is None else settings
        self.base_url = self.settings.get("MIMI_KNOWLEDGE_URL", "http://127.0.0.1:5142").rstrip("/")
        validate_url(self.base_url)
        self.timeout = timeout
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

    def request(self, method, route, payload=None, write=False):
        token_name = "MIMI_KNOWLEDGE_WRITE_TOKEN" if write else "MIMI_KNOWLEDGE_READ_TOKEN"
        token = self.settings.get(token_name)
        if not token or any(character in token for character in "\r\n"):
            raise ClientError("missing_or_invalid_" + token_name)
        data = None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8")
        request = urllib.request.Request(self.base_url + route, data=data, method=method,
                                         headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"})
        try:
            with self.opener.open(request, timeout=self.timeout) as response:
                raw = response.read(4 * 1024 * 1024 + 1)
                if len(raw) > 4 * 1024 * 1024:
                    raise ClientError("response_too_large")
                return json.loads(raw)
        except urllib.error.HTTPError as error:
            raise ClientError("http_" + str(error.code)) from None
        except (urllib.error.URLError, TimeoutError, OSError):
            raise ClientError("connection_failed_or_timed_out") from None
        except (ValueError, UnicodeError):
            raise ClientError("invalid_json_response") from None

    def context(self, payload):
        return self.request("POST", "/api/knowledge/context", payload)

    def capture(self, payload):
        return self.request("POST", "/api/knowledge/captures", payload, write=True)

    def receipt(self, capture_id):
        return self.request("GET", "/api/knowledge/captures/" + capture_segment(capture_id))

    def clarify(self, capture_id, payload):
        return self.request("POST", "/api/knowledge/captures/" + capture_segment(capture_id) + "/clarifications", payload, write=True)

    def reconcile(self, capture_id, payload):
        if payload.get("acknowledgeRestore") is not True:
            raise ClientError("explicit_restore_acknowledgement_required")
        for field in ("previousCorpusEpoch", "expectedCorpusEpoch"):
            if not payload.get(field) or capture_segment(payload[field]) == str(uuid.UUID(int=0)):
                raise ClientError("explicit_reconciliation_epochs_required")
        if not isinstance(payload.get("idempotencyKey"), str) or not payload["idempotencyKey"].strip():
            raise ClientError("reconciliation_idempotency_key_required")
        return self.request("POST", "/api/knowledge/captures/" + capture_segment(capture_id) + "/reconcile", payload, write=True)

    def poll(self, capture_id, seconds=30, interval=1):
        deadline = time.monotonic() + seconds
        while True:
            receipt = self.receipt(capture_id)
            if receipt.get("status") not in {"received", "processing"} or time.monotonic() >= deadline:
                return receipt
            time.sleep(min(interval, max(0, deadline - time.monotonic())))


def validate_url(value):
    try:
        parsed = urllib.parse.urlsplit(value)
        port = parsed.port
    except ValueError:
        raise ClientError("invalid_endpoint") from None
    if not parsed.hostname or parsed.username is not None or parsed.password is not None or parsed.query or parsed.fragment:
        raise ClientError("endpoint_credentials_or_query_refused")
    if parsed.path not in {"", "/"} or parsed.scheme not in {"http", "https"}:
        raise ClientError("invalid_endpoint")
    if parsed.scheme == "http" and parsed.hostname not in {"localhost", "127.0.0.1", "::1"}:
        raise ClientError("non_loopback_http_refused_use_https_or_ssh_tunnel")
    return port


def capture_segment(value):
    try:
        return str(uuid.UUID(value))
    except (ValueError, TypeError, AttributeError):
        raise ClientError("invalid_capture_id") from None


def task_namespace(task_id, payload):
    if not isinstance(task_id, str) or not task_id.strip() or len(task_id) > 200:
        raise ClientError("task_id_required")
    if payload.get("sourceNamespace") not in {None, task_id}:
        raise ClientError("source_namespace_must_match_task_id")
    return dict(payload, sourceNamespace=task_id)


def stable_handoff(task_id, payload, state):
    payload = task_namespace(task_id, payload)
    messages = payload.get("messages", [])
    if not messages or any(not item.get("id") or not item.get("role") or not isinstance(item.get("order"), int) or not isinstance(item.get("text"), str) for item in messages):
        raise ClientError("stable_attributed_messages_required")
    if len({item["id"] for item in messages}) != len(messages):
        raise ClientError("duplicate_message_ids")
    if state and state.get("taskId") != task_id:
        raise ClientError("state_belongs_to_another_task")
    acknowledged = set(state.get("messageIds", []))
    remaining = [item for item in messages if item["id"] not in acknowledged]
    if not remaining:
        return None
    result = dict(payload)
    result["messages"] = remaining
    result["previousCursor"] = state.get("cursor")
    digest = hashlib.sha256(json.dumps({"taskId": task_id, "payload": result}, sort_keys=True, ensure_ascii=False).encode()).hexdigest()
    result["idempotencyKey"] = "handoff-" + digest
    return result


def save_ack(path, task_id, previous, payload, receipt):
    if not receipt.get("id") or not receipt.get("cursor") or receipt.get("status") not in {"received", "processing", "processed", "needs_input", "partial", "failed"}:
        raise ClientError("invalid_durable_receipt")
    state = {"taskId": task_id, "captureId": receipt["id"], "cursor": receipt["cursor"],
             "messageIds": sorted(set(previous.get("messageIds", [])) | {item["id"] for item in payload["messages"]})}
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    temporary.write_text(json.dumps(state), encoding="utf-8")
    os.replace(temporary, destination)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=["context", "capture", "receipt", "clarify", "reconcile"])
    parser.add_argument("--input", help="JSON file; defaults to stdin")
    parser.add_argument("--capture-id")
    parser.add_argument("--poll-seconds", type=int, default=0)
    parser.add_argument("--state", help="cursor state file outside source control")
    parser.add_argument("--task-id", help="stable task/conversation source namespace; required with --state, maximum 200 characters")
    args = parser.parse_args()
    try:
        if not 0 <= args.poll_seconds <= 60:
            raise ClientError("poll_seconds_must_be_0_to_60")
        client = Client()
        payload = {} if args.operation == "receipt" else json.loads(Path(args.input).read_text(encoding="utf-8") if args.input else sys.stdin.read())
        previous = {}
        if args.state:
            if args.operation != "capture":
                raise ClientError("state_supported_for_capture_only")
            if Path(args.state).exists():
                previous = json.loads(Path(args.state).read_text(encoding="utf-8"))
            payload = stable_handoff(args.task_id, payload, previous)
            if payload is None:
                print(json.dumps({"status": "already_acknowledged", "captureId": previous.get("captureId")}))
                return 0
        elif args.operation == "capture" and args.task_id is not None:
            payload = task_namespace(args.task_id, payload)
        if args.operation == "context":
            result = client.context(payload)
        elif args.operation == "capture":
            result = client.capture(payload)
            if args.state:
                save_ack(args.state, args.task_id, previous, payload, result)
        elif args.operation == "clarify":
            result = client.clarify(args.capture_id, payload)
        elif args.operation == "reconcile":
            result = client.reconcile(args.capture_id, payload)
        else:
            result = client.receipt(args.capture_id)
        if args.poll_seconds and args.operation != "context":
            result = client.poll(result["id"], args.poll_seconds)
        print(json.dumps(result, ensure_ascii=False))
        return 0
    except (ClientError, OSError, ValueError, KeyError, TypeError) as error:
        code = str(error) if isinstance(error, ClientError) else "invalid_input_or_local_state"
        print(json.dumps({"errorClass": code}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
