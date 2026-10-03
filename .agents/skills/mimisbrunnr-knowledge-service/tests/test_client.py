import importlib.util
import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer


MODULE = Path(__file__).resolve().parents[1] / "scripts" / "knowledge_client.py"
spec = importlib.util.spec_from_file_location("knowledge_client", MODULE)
client = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client)


class TransportTests(unittest.TestCase):
    def setUp(self):
        self.calls = []
        calls = self.calls

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_POST(self):
                calls.append((self.path, self.headers.get("Authorization"), json.loads(self.rfile.read(int(self.headers["Content-Length"])))))
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b'{"status":"received"}')

            def do_GET(self):
                calls.append((self.path, self.headers.get("Authorization"), None))
                self.send_response(302)
                self.send_header("Location", "http://127.0.0.1:1/stolen")
                self.end_headers()

        self.server = HTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.client = client.Client({"MIMI_KNOWLEDGE_URL": "http://127.0.0.1:" + str(self.server.server_port),
                                     "MIMI_KNOWLEDGE_READ_TOKEN": "synthetic-read", "MIMI_KNOWLEDGE_WRITE_TOKEN": "synthetic-write"})

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()

    def test_operation_capabilities_and_contract(self):
        self.client.context({"question": "refund"})
        self.client.capture({"idempotencyKey": "fixed", "messages": []})
        self.client.clarify("c541d950-1c7d-4627-ab2d-d091375f7e7f", {"messages": []})
        self.assertEqual([item[1] for item in self.calls], ["Bearer synthetic-read", "Bearer synthetic-write", "Bearer synthetic-write"])
        self.assertEqual(self.calls[0][0], "/api/knowledge/context")

    def test_redirect_cannot_forward_credentials(self):
        with self.assertRaisesRegex(client.ClientError, "redirect_refused"):
            self.client.receipt("c541d950-1c7d-4627-ab2d-d091375f7e7f")
        self.assertEqual(len(self.calls), 1)

    def test_missing_capability_fails_before_network(self):
        self.client.settings.pop("MIMI_KNOWLEDGE_WRITE_TOKEN")
        with self.assertRaises(client.ClientError):
            self.client.capture({})
        self.assertEqual(self.calls, [])

    def test_task_namespace_reaches_capture_transport(self):
        payload = client.task_namespace("conversation-123", {"idempotencyKey": "fixed", "messages": []})
        self.client.capture(payload)
        self.assertEqual(self.calls[0][2]["sourceNamespace"], "conversation-123")
        self.assertEqual(self.calls[0][2]["idempotencyKey"], "fixed")

    def test_explicit_reconciliation_uses_write_auth_without_automatic_allowance(self):
        payload = {"idempotencyKey": "explicit-restore-1", "previousCorpusEpoch": "ce59eca2-aebb-4b34-b4bd-306386ca38d2",
            "expectedCorpusEpoch": "68d4807b-4315-4c4b-9590-8bf77f1d41ea", "acknowledgeRestore": True}
        self.client.reconcile("c541d950-1c7d-4627-ab2d-d091375f7e7f", payload)
        self.assertTrue(self.calls[0][0].endswith("/reconcile"))
        self.assertEqual(self.calls[0][1], "Bearer synthetic-write")
        self.assertEqual(self.calls[0][2], payload)
        self.assertNotIn("additionalBudget", self.calls[0][2])
        self.client.reconcile("c541d950-1c7d-4627-ab2d-d091375f7e7f", payload)
        self.assertEqual(self.calls[0][2], self.calls[1][2])

    def test_reconciliation_requires_acknowledgement_and_both_epochs_before_network(self):
        base = {"idempotencyKey": "restore", "previousCorpusEpoch": "ce59eca2-aebb-4b34-b4bd-306386ca38d2",
            "expectedCorpusEpoch": "68d4807b-4315-4c4b-9590-8bf77f1d41ea", "acknowledgeRestore": True}
        for field in ["acknowledgeRestore", "previousCorpusEpoch", "expectedCorpusEpoch", "idempotencyKey"]:
            invalid = {key: value for key, value in base.items() if key != field}
            with self.assertRaises(client.ClientError):
                self.client.reconcile("c541d950-1c7d-4627-ab2d-d091375f7e7f", invalid)
        self.assertEqual(self.calls, [])


class IdentityTests(unittest.TestCase):
    def test_uncertain_send_retry_and_incremental_ack(self):
        first = {"task": "spec", "messages": [{"id": "m1", "role": "user", "order": 1, "text": "Approved, unshipped"}]}
        payload = client.stable_handoff("task-a", first, {})
        self.assertEqual(payload, client.stable_handoff("task-a", first, {}))
        self.assertEqual(payload["sourceNamespace"], "task-a")
        self.assertNotIn("sourceNamespace", first)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.json"
            client.save_ack(path, "task-a", {}, payload, {"id": "c541d950-1c7d-4627-ab2d-d091375f7e7f", "cursor": "r1", "status": "received"})
            state = json.loads(path.read_text())
            self.assertNotIn("Approved", path.read_text())
            self.assertIsNone(client.stable_handoff("task-a", first, state))
            later = dict(first, messages=first["messages"] + [{"id": "m2", "role": "user", "order": 2, "text": "Correction"}])
            delta = client.stable_handoff("task-a", later, state)
            self.assertEqual([item["id"] for item in delta["messages"]], ["m2"])
            self.assertEqual(delta["previousCursor"], "r1")
            self.assertEqual(delta["sourceNamespace"], payload["sourceNamespace"])
            self.assertNotEqual(payload["idempotencyKey"], delta["idempotencyKey"])
            with self.assertRaises(client.ClientError):
                client.stable_handoff("task-b", first, state)

    def test_separate_tasks_cannot_collide_on_message_identity(self):
        evidence = {"task": "spec", "messages": [{"id": "m1", "role": "user", "order": 1, "text": "Approved future intent"}]}
        first = client.stable_handoff("conversation-a", evidence, {})
        second = client.stable_handoff("conversation-b", evidence, {})
        self.assertNotEqual(first["sourceNamespace"], second["sourceNamespace"])
        self.assertNotEqual(first["idempotencyKey"], second["idempotencyKey"])
        self.assertEqual(first["messages"][0]["id"], second["messages"][0]["id"])

    def test_conflicting_or_invalid_namespace_refused(self):
        for task in [None, "", " ", "a" * 201]:
            with self.assertRaises(client.ClientError):
                client.task_namespace(task, {})
        with self.assertRaisesRegex(client.ClientError, "source_namespace_must_match_task_id"):
            client.task_namespace("task-a", {"sourceNamespace": "task-b"})
        self.assertEqual(client.task_namespace("task-a", {"sourceNamespace": "task-a"})["sourceNamespace"], "task-a")

    def test_bad_ack_does_not_advance_cursor(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.json"
            with self.assertRaises(client.ClientError):
                client.save_ack(path, "a", {}, {"messages": []}, {"status": "received"})
            self.assertFalse(path.exists())

    def test_endpoint_and_route_injection_refused(self):
        for endpoint in ["http://secret@localhost", "http://localhost?key=secret", "http://remote-host", "ftp://localhost", "http://localhost/path"]:
            with self.assertRaises(client.ClientError):
                client.validate_url(endpoint)
        with self.assertRaises(client.ClientError):
            client.capture_segment("../../snapshot")

    def test_protected_file_environment_precedence(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "credentials"
            path.write_text("MIMI_KNOWLEDGE_READ_TOKEN=synthetic-file\nUNRELATED_KEY=ignored\n")
            if os.name != "nt":
                path.chmod(0o600)
            settings = client.configuration({"MIMI_KNOWLEDGE_CREDENTIALS_FILE": str(path), "MIMI_KNOWLEDGE_READ_TOKEN": "synthetic-env"})
            self.assertEqual(settings["MIMI_KNOWLEDGE_READ_TOKEN"], "synthetic-env")
            self.assertNotIn("UNRELATED_KEY", settings)


if __name__ == "__main__":
    unittest.main()
