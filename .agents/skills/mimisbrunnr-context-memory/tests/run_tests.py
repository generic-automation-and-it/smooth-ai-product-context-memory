#!/usr/bin/env python3
"""L0 committed harness for the mimisbrunnr-context-memory skill plumbing.

Standard-library unittest only — no external test runner dependency. Exercises the deterministic
artefacts (redact.py, atomicity.py) over explicit positive and negative fixtures, asserting real
behaviour rather than re-deriving the rules (the anti-pattern run-trial.js fell into).

Run: python3 tests/run_tests.py
"""

import importlib.util
import copy
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stdout, redirect_stderr
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
SCRIPTS = HERE.parent / "scripts"
sys.path.insert(0, str(SCRIPTS))


def _load(name):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


redact = _load("redact")
atomicity = _load("atomicity")
client = _load("context_memory_client")
near_miss = _load("near_miss_tags")
deepsearch = _load("deepsearch")
divergence = _load("divergence")
read_mcp = _load("memory_read_mcp")
write_mcp = _load("memory_write_mcp")


def _run_atomicity(batch):
    """Drive atomicity.py end to end, so the description/statement split is covered, not just classify()."""
    completed = subprocess.run(
        [sys.executable, str(SCRIPTS / "atomicity.py")],
        input=json.dumps(batch),
        capture_output=True,
        text=True,
        check=True,
    )
    return json.loads(completed.stdout)


def _scrub_item(content):
    redacted, findings = redact._scrub(content)
    return {
        "redacted": redacted,
        "findings": [{"rule_name": name, "hit_count": count} for name, count in sorted(findings.items())],
    }


class RedactTests(unittest.TestCase):
    def test_planted_aws_key_never_leaks(self):
        item = "The deployment uses AKIAIOSFODNN7EXAMPLE for CI."
        result = _scrub_item(item)
        self.assertNotIn("AKIAIOSFODNN7EXAMPLE", result["redacted"])
        self.assertTrue(any(f["rule_name"] == "aws-access-key-id" for f in result["findings"]))

    def test_planted_github_token_never_leaks(self):
        item = "The runner uses ghp_1234567890abcdefghijklmnopqrstuvwxyz for push."
        result = _scrub_item(item)
        self.assertNotIn("ghp_", result["redacted"])
        self.assertTrue(any(f["rule_name"] == "github-token" for f in result["findings"]))

    def test_connection_string_password_scrubbed(self):
        item = 'Server=db;Port=5432;User Id=sa;Password=Sup3rS3cret!;Database=app'
        result = _scrub_item(item)
        self.assertNotIn("Sup3rS3cret!", result["redacted"])
        self.assertTrue(any(f["rule_name"] == "connection-string-password" for f in result["findings"]))

    def test_private_key_pem_scrubbed(self):
        item = "-----BEGIN RSA PRIVATE KEY-----\nMIICXg==\n-----END RSA PRIVATE KEY-----"
        result = _scrub_item(item)
        self.assertNotIn("MIICXg==", result["redacted"])
        self.assertTrue(any(f["rule_name"] == "private-key-pem" for f in result["findings"]))

    def test_finding_reports_rule_name_only(self):
        item = "token: abcdef1234567890"
        result = _scrub_item(item)
        for f in result["findings"]:
            self.assertIn("rule_name", f)
            # The matched span must never appear in the finding.
            self.assertNotIn("abcdef1234567890", json.dumps(f))

    def test_clean_content_has_no_findings(self):
        item = "PostgreSQL stores our search index."
        result = _scrub_item(item)
        self.assertEqual(result["redacted"], item)
        self.assertEqual(result["findings"], [])

    def test_redact_and_flag_never_rejects(self):
        # A leak is scrubbed and recorded, not treated as a reason to drop the fact.
        item = "The deployment uses DEPLOY_TOKEN=abcdef1234567890 for auth."
        result = _scrub_item(item)
        self.assertIsNotNone(result["redacted"])
        self.assertNotIn("abcdef1234567890", result["redacted"])
        self.assertNotIn("abcdef1234567890", json.dumps(result["findings"]))
        self.assertTrue(any(f["rule_name"] == "generic-secret-assignment" for f in result["findings"]))


class AtomicityTests(unittest.TestCase):
    def test_single_atomic_fact_is_simple(self):
        verdict = atomicity.classify("PostgreSQL stores our search index.")
        self.assertEqual(verdict["verdict"], "simple")

    def test_bundled_tally_is_flagged(self):
        verdict = atomicity.classify("Three things matter: the API, the storage, and the model.")
        self.assertEqual(verdict["verdict"], "bundled")

    def test_bundled_compound_is_flagged(self):
        verdict = atomicity.classify(
            "We chose PostgreSQL, and it also explains why we need JSONB, but it further means the index is GIN."
        )
        self.assertEqual(verdict["verdict"], "bundled")

    def test_clean_single_claim_with_conjunction_stays_simple(self):
        # One "and" between attributes is not a bundle signal.
        verdict = atomicity.classify("The service is local-first and private.")
        self.assertEqual(verdict["verdict"], "simple")

    def test_empty_input_is_simple(self):
        verdict = atomicity.classify("")
        self.assertEqual(verdict["verdict"], "simple")

    def test_reason_clause_is_one_fact(self):
        # A fact plus the reason it holds is one fact. Scoring "because" as a claim junction made
        # this a false positive on a real braindump batch.
        verdict = atomicity.classify(
            "PostgreSQL is selected because typed indexed relations and JSONB support server-side "
            "querying of rich evolving metadata."
        )
        self.assertEqual(verdict["verdict"], "simple")
        self.assertEqual(verdict["signals"], [])

    def test_coordinated_pair_is_one_fact(self):
        verdict = atomicity.classify(
            "The memory service is a local HTTP Docker API consumed manually through an AI harness "
            "skill for both capture and retrieval."
        )
        self.assertEqual(verdict["verdict"], "simple")

    def test_semicolon_between_clauses_is_bundled(self):
        verdict = atomicity.classify(
            "One memory represents one atomic fact; retrieval returns arrays of statements."
        )
        self.assertEqual(verdict["verdict"], "bundled")

    def test_single_contrastive_junction_is_bundled(self):
        verdict = atomicity.classify(
            "Groups provide stable episodic umbrellas while append-only versions preserve changed "
            "child-memory claims."
        )
        self.assertEqual(verdict["verdict"], "bundled")

    def test_noun_phrase_list_alone_is_not_bundled(self):
        verdict = atomicity.classify("The store keeps facets, tags, and scope dimensions.")
        self.assertEqual(verdict["verdict"], "simple")

    def test_signal_set_discriminates_between_verdicts(self):
        # The regression this guards: every candidate of a real batch carried the same signal, so
        # the signal list said nothing about the verdict.
        simple = atomicity.classify("Capture derives a content summary so retrieval stays compact.")
        bundled = atomicity.classify("Facets filter dependably while free tags stay expressive.")
        self.assertEqual(simple["signals"], [])
        self.assertIn("discourse", bundled["signals"])

    def test_verdict_scores_the_statement_not_the_description(self):
        # A description is a subject label; coordination inside it ("labels and tags") is not a
        # second claim and must not push the candidate to bundled.
        batch = [{"description": "Controlled labels and free tags", "statement": "Facets filter."}]
        result = _run_atomicity(batch)
        self.assertEqual(result["results"][0]["verdict"], "simple")

    def test_description_is_scored_when_no_statement_is_supplied(self):
        batch = [{"description": "Facets filter dependably while free tags stay expressive."}]
        result = _run_atomicity(batch)
        self.assertEqual(result["results"][0]["verdict"], "bundled")


class PathsClientTests(unittest.TestCase):
    FIXTURES = HERE / "fixtures"

    def _args(self, payload_path):
        return SimpleNamespace(payload=str(payload_path))

    def test_plain_and_filtered_request_shapes_carry_required_fields(self):
        for name in ("paths_plain.json", "paths_filtered.json"):
            with self.subTest(name=name):
                request = json.loads((self.FIXTURES / name).read_text(encoding="utf-8"))["request"]
                self.assertIn("sourceUuid", request)
                self.assertIn("maxDepth", request)

    def test_filtered_request_reaches_all_filters(self):
        request = json.loads((self.FIXTURES / "paths_filtered.json").read_text(encoding="utf-8"))["request"]
        for field in ("sourceUuid", "maxDepth", "targetUuid", "relation", "direction", "kind", "status", "scopeDimension", "limit"):
            self.assertIn(field, request)

    def test_render_path_matches_expected_summary(self):
        data = json.loads((self.FIXTURES / "paths_plain.json").read_text(encoding="utf-8"))
        path = data["response"]["paths"][0]
        self.assertEqual(client._render_path(path), data["expected_summary"])

    def test_missing_max_depth_is_rejected_before_any_network_call(self):
        for bad in ({"sourceUuid": "aaaaaaaa-0000-0000-0000-000000000000"},
                    {"sourceUuid": "aaaaaaaa-0000-0000-0000-000000000000", "maxDepth": None},
                    {"sourceUuid": "aaaaaaaa-0000-0000-0000-000000000000", "maxDepth": 0},
                    {"sourceUuid": "aaaaaaaa-0000-0000-0000-000000000000", "maxDepth": "2"},
                    {"sourceUuid": "aaaaaaaa-0000-0000-0000-000000000000", "maxDepth": True}):
            with self.subTest(payload=bad):
                payload_path = self._write_temp(bad)
                try:
                    with self.assertRaises(client.ClientError) as ctx:
                        client.cmd_paths(self._args(payload_path))
                    self.assertIn("maxDepth", str(ctx.exception))
                finally:
                    os.unlink(payload_path)

    def test_missing_source_uuid_is_rejected_before_any_network_call(self):
        for bad in ({"maxDepth": 2}, {"maxDepth": 2, "sourceUuid": None}, {"maxDepth": 2, "sourceUuid": "  "}):
            with self.subTest(payload=bad):
                payload_path = self._write_temp(bad)
                try:
                    with self.assertRaises(client.ClientError) as ctx:
                        client.cmd_paths(self._args(payload_path))
                    self.assertIn("sourceUuid", str(ctx.exception))
                finally:
                    os.unlink(payload_path)

    def test_unknown_source_uuid_error_shape_is_documented(self):
        data = json.loads((self.FIXTURES / "paths_error_unknown_source.json").read_text(encoding="utf-8"))
        self.assertEqual(data["http_status"], 404)
        self.assertEqual(data["client_exit_code"], 1)
        self.assertTrue(data["client_error_prefix"].startswith("HTTP 404"))

    @staticmethod
    def _write_temp(obj):
        handle = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8")
        with handle:
            json.dump(obj, handle)
        return handle.name


class WritePayloadTests(unittest.TestCase):
    def test_set_preserves_create_uuid_payload(self):
        create_uuid = "11111111-1111-4111-8111-111111111111"
        payload = {"items": [{"uuid": None, "createUuid": create_uuid}], "links": []}
        with patch.object(client, "read_payload", return_value=copy.deepcopy(payload)), \
                patch.object(client, "_request", return_value={"created": 1}) as request, \
                redirect_stdout(io.StringIO()):
            client.cmd_set(SimpleNamespace(payload=None, dryrun=True))
        self.assertEqual(request.call_args.args[2]["items"][0]["createUuid"], create_uuid)
        self.assertEqual(request.call_args.kwargs["query"], {"dryRun": "true"})

    def test_write_mcp_enforces_checkpoint_cap_before_transport(self):
        payload = {"items": [{}] * (client.MAX_CANDIDATES + 1)}
        with patch.object(write_mcp.client, "_request") as request, \
                self.assertRaises(write_mcp.client.ClientError):
            write_mcp.call_tool("set", {"payload": payload})
        request.assert_not_called()

    def test_missing_capability_fails_before_transport(self):
        with patch.dict(os.environ, {}, clear=True), \
                patch.object(client, "_open") as transport:
            with self.assertRaises(client.ClientError) as error:
                client._request("POST", "/api/context/query", {})
        self.assertIn(client.ENV_READ_TOKEN, str(error.exception))
        transport.assert_not_called()

    def test_read_and_write_routes_select_distinct_credentials(self):
        with patch.dict(os.environ, {client.ENV_READ_TOKEN: "read-only",
                                    client.ENV_WRITE_TOKEN: "write-only"}), \
                patch.object(client, "_open") as transport:
            transport.return_value.__enter__.return_value.read.return_value = b'{}'
            client._request("POST", "/api/context/query", {})
            read_request = transport.call_args.args[0]
            client._request("POST", "/api/context/memories", {"items": []})
            write_request = transport.call_args.args[0]
            client._request("POST", "/api/context/preflight", {"candidates": []})
            preflight_request = transport.call_args.args[0]
        self.assertEqual(read_request.get_header("Authorization"), "Bearer read-only")
        self.assertEqual(write_request.get_header("Authorization"), "Bearer write-only")
        self.assertEqual(preflight_request.get_header("Authorization"), "Bearer write-only")

    def test_base_url_rejects_remote_plain_http_and_embedded_credentials(self):
        for value in ("http://example.com", "https://example.com", "https://user:pass@example.com", "https://example.com/path"):
            with self.subTest(value=value), patch.dict(os.environ, {client.ENV_BASE_URL: value}):
                with self.assertRaises(client.ClientError):
                    client.base_url()

    def test_redirects_are_refused(self):
        handler = client._NoRedirect()
        request = client.urllib.request.Request(
            "https://memory.example/api/context/query",
            headers={"Authorization": "Bearer secret"})
        with self.assertRaises(client.ClientError):
            handler.redirect_request(request, None, 302, "Found", {}, "https://evil.example/")


class DeepSearchTests(unittest.TestCase):
    def test_default_client_query_has_no_deepsearch_pass(self):
        with patch.object(client, "read_payload", return_value={}), \
                patch.object(client, "_request", return_value={"items": []}) as request, \
                redirect_stdout(io.StringIO()):
            client.cmd_query(SimpleNamespace(payload=None))
        request.assert_called_once_with("POST", "/api/context/query", {"limit": 200})

    def test_deepsearch_is_bounded_deduplicated_and_disclosed(self):
        baseline_rows = [self.row(index) for index in range(200)]
        calls = []

        def request(method, path, payload):
            calls.append((method, path, payload))
            if path.endswith("query") and payload.get("query") is None:
                return {"items": baseline_rows}
            if path.endswith("query"):
                return {"items": [baseline_rows[0], self.row(200 + len(calls))]}
            return {"paths": [{"endpoint": self.row(300 + len(calls))}]}

        result = deepsearch.execute(
            {"baseline": {"facets": ["storage"], "kind": "decision"},
             "keywords": ["zeta", "alpha", "beta", "gamma", "delta"]},
            request=request)

        query_calls = [call for call in calls if call[1].endswith("query")]
        path_calls = [call for call in calls if call[1].endswith("paths")]
        self.assertEqual(len(query_calls), 5)
        self.assertEqual([call[2]["query"] for call in query_calls[1:]],
                         ["alpha", "beta", "delta", "gamma"])
        self.assertEqual(len(path_calls), 5)
        self.assertTrue(all(call[2]["maxDepth"] == 1 and call[2]["limit"] == 20
                            for call in path_calls))
        self.assertLessEqual(len(result["items"]), deepsearch.AGGREGATE_LIMIT)
        self.assertTrue(result["disclosure"]["possiblyOmitted"])
        self.assertEqual(result["disclosure"]["keywordsOmittedByCap"], 1)
        self.assertGreater(result["disclosure"]["anchorsOmittedByCap"], 0)
        keys = [(item["uuid"], item["version"]) for item in result["items"]]
        self.assertEqual(len(keys), len(set(keys)))

    def test_deepsearch_rejects_sentence_queries(self):
        with self.assertRaises(ValueError):
            deepsearch.execute(
                {"baseline": {}, "keywords": ["this is a whole sentence"]},
                request=lambda *_: {"items": []})

    def test_group_context_does_not_broaden_into_unscoped_traversal(self):
        calls = []
        result = deepsearch.execute(
            {"baseline": {"groupUuid": "11111111-1111-4111-8111-111111111111"}, "keywords": []},
            request=lambda method, path, payload: calls.append((method, path, payload))
                or ({"items": [self.row(1)]} if path.endswith("query") else {"paths": []}))
        self.assertFalse(any(path.endswith("paths") for _, path, _ in calls))
        self.assertTrue(result["disclosure"]["traversalSkippedForContextSelector"])
        self.assertTrue(result["disclosure"]["possiblyOmitted"])

    @staticmethod
    def row(index):
        return {"uuid": f"00000000-0000-4000-8000-{index:012d}", "version": 1}


class DivergenceTests(unittest.TestCase):
    def payload(self):
        candidate = {
            "createUuid": "11111111-1111-4111-8111-111111111111",
            "kind": "decision", "scopeDimension": "product", "scopeIdentifier": None,
            "facets": ["storage"], "tags": [], "confidence": 80,
            "validFrom": "2026-09-17T00:00:00Z", "summaryModel": "model",
            "summaryPromptVersion": "v1",
        }
        return {
            "candidate": {"scopeDimension": "product", "scopeIdentifier": None, "write": candidate},
            "existing": {"uuid": "22222222-2222-4222-8222-222222222222", "version": 3,
                         "kind": "decision", "scopeDimension": "product", "scopeIdentifier": None,
                         "confidence": 70},
            "reason": "No stated authority selects either claim.",
            "existingPairs": [],
        }

    def test_genuine_conflict_composes_proposed_record_and_two_links(self):
        result = divergence.compose(self.payload())
        self.assertEqual(result["diverged"], 1)
        self.assertEqual(result["items"][1]["kind"], "divergence")
        self.assertEqual(result["items"][1]["status"], "proposed")
        self.assertEqual(len(result["links"]), 2)
        self.assertTrue(all(link["relation"] == "contradicts" for link in result["links"]))

    def test_existing_pair_and_divergence_evidence_do_not_recurse(self):
        first = divergence.compose(self.payload())
        duplicate = self.payload()
        duplicate["existingPairs"] = [first["pair"]]
        self.assertEqual(divergence.compose(duplicate)["diverged"], 0)
        loop = self.payload()
        loop["existing"]["kind"] = "divergence"
        with self.assertRaises(ValueError):
            divergence.compose(loop)

        persisted = self.payload()
        persisted["existingDivergences"] = [{
            "uuid": divergence.divergence_uuid(first["pair"]),
            "kind": "divergence", "status": "proposed",
            "sources": [
                {"kind": "memory", "reference": "22222222-2222-4222-8222-222222222222:v3"},
                {"kind": "memory", "reference": "11111111-1111-4111-8111-111111111111:v1"},
            ],
        }]
        self.assertEqual(divergence.compose(persisted)["diverged"], 0)

    def test_scope_mismatch_is_not_a_conflict(self):
        payload = self.payload()
        payload["existing"]["scopeDimension"] = "program"
        with self.assertRaises(ValueError):
            divergence.compose(payload)


class AgentContractTests(unittest.TestCase):
    AGENTS = HERE.parent / "agents"
    MUTATIONS = ("set", "create-link", "resolve-group", "update-group", "append-description",
                 "propose-label", "upsert-initiative", "ticket-parent")

    def test_read_agent_grants_only_read_client(self):
        text = (self.AGENTS / "memory-read.md").read_text(encoding="utf-8")
        grant = text.split("---", 2)[1]
        self.assertIn("mcp__mimisbrunnr-read__query", grant)
        self.assertNotIn("Bash", grant)
        self.assertNotIn("context_memory_client.py:*", grant)
        for mutation in self.MUTATIONS:
            self.assertNotIn(mutation, grant)

        registration = (HERE.parents[2] / "agents" / "memory-read.md").read_text(encoding="utf-8")
        self.assertIn("mcp__mimisbrunnr-read__query", registration)
        self.assertNotIn("Bash", registration.split("---", 2)[1])
        self.assertIn("CONTEXT_MEMORY_WRITE_TOKEN", registration)

    def test_read_client_refuses_environment_with_write_credential(self):
        completed = subprocess.run(
            [sys.executable, str(SCRIPTS / "context_memory_read_client.py"), "probe"],
            env={**os.environ, client.ENV_READ_TOKEN: "read", client.ENV_WRITE_TOKEN: "write"},
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(completed.returncode, 2)
        self.assertIn(client.ENV_WRITE_TOKEN, completed.stderr)

    def test_read_mcp_lists_no_mutation_tools(self):
        names = {tool["name"] for tool in read_mcp.tool_definitions()}
        self.assertEqual(names, {"probe", "query", "deepsearch", "get_versions", "get_blob",
                                 "paths", "ticket_paths", "labels", "initiatives"})
        for mutation in self.MUTATIONS:
            self.assertNotIn(mutation.replace("-", "_"), names)

    def test_read_mcp_removes_write_credential_at_startup(self):
        completed = subprocess.run(
            [sys.executable, str(SCRIPTS / "memory_read_mcp.py")],
            input=json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/list"}) + "\n",
            env={**os.environ, client.ENV_READ_TOKEN: "read", client.ENV_WRITE_TOKEN: "write"},
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(completed.returncode, 0)
        response = json.loads(completed.stdout)
        self.assertEqual(response["id"], 1)
        self.assertEqual(len(response["result"]["tools"]), 9)

    def test_read_mcp_configuration_exists(self):
        config = json.loads((HERE.parents[3] / ".mcp.json").read_text(encoding="utf-8"))
        command = config["mcpServers"]["mimisbrunnr-read"]
        self.assertEqual(command["command"], "python3")
        self.assertEqual(command["args"], [
            ".agents/skills/mimisbrunnr-context-memory/scripts/memory_read_mcp.py"
        ])

    def test_read_mcp_lifecycle_initialize_ping_and_list(self):
        initialized = read_mcp.handle({"jsonrpc": "2.0", "id": 1, "method": "initialize"})
        pinged = read_mcp.handle({"jsonrpc": "2.0", "id": 2, "method": "ping"})
        listed = read_mcp.handle({"jsonrpc": "2.0", "id": 3, "method": "tools/list"})
        self.assertEqual(initialized["result"]["serverInfo"]["name"], "mimisbrunnr-read")
        self.assertEqual(pinged["result"], {})
        self.assertEqual(len(listed["result"]["tools"]), 9)

    def test_agents_and_orchestration_contract_exist(self):
        read = (self.AGENTS / "memory-read.md").read_text(encoding="utf-8")
        write = (self.AGENTS / "memory-write.md").read_text(encoding="utf-8")
        skill = (HERE.parent / "SKILL.md").read_text(encoding="utf-8")
        for marker in ("lookup", "grounding", "ALSO IN STORE"):
            self.assertIn(marker, read)
        for stage in ("Preflight", "Redact", "Dedupe / derive links", "Atomicity check", "Write"):
            self.assertIn(stage, write)
        self.assertIn("Where Each Step Runs", skill)
        self.assertIn("Never call the store client", skill)
        registration = (HERE.parents[2] / "agents" / "memory-write.md").read_text(encoding="utf-8")
        self.assertIn("mcp__mimisbrunnr-write__set", registration)
        self.assertNotIn("Bash", registration.split("---", 2)[1])
        declared = {line.strip()[2:] for line in registration.split("---", 2)[1].splitlines()
                    if line.strip().startswith("- mcp__mimisbrunnr-write__")}
        exposed = {f"mcp__mimisbrunnr-write__{tool['name']}" for tool in write_mcp.tool_definitions()}
        self.assertEqual(declared, exposed)


class TicketClientTests(unittest.TestCase):
    CHILD = {"provider": "GitHub ", "key": " 42"}
    PARENT = {"provider": "github", "key": "10"}

    def parent_payload(self):
        return {"child": self.CHILD, "parent": self.PARENT, "expectedParent": None,
                "reason": "Practitioner declared this parent.", "source": "Checkpoint declaration",
                "observedAt": "2026-09-14T12:00:00Z"}

    def invoke(self, command, payload, response=None, dryrun=False):
        output = io.StringIO()
        with patch.object(client, "read_payload", return_value=copy.deepcopy(payload)), \
                patch.object(client, "_request", return_value=response) as request, redirect_stdout(output):
            result = command(SimpleNamespace(payload=None, dryrun=dryrun))
        self.assertEqual(json.loads(output.getvalue()), result)
        return result, request

    def assert_rejected(self, command, payload, dryrun=False):
        with patch.object(client, "read_payload", return_value=payload), \
                patch.object(client, "_request") as request:
            with self.assertRaises(client.ClientError):
                command(SimpleNamespace(payload=None, dryrun=dryrun))
            request.assert_not_called()

    def test_parent_set_reparent_remove_exact_transport_and_receipt(self):
        for parent, expected in ((self.PARENT, None), (self.PARENT, {"provider": "jira", "key": "OLD-1"}),
                                 (None, self.PARENT)):
            payload = self.parent_payload() | {"parent": parent, "expectedParent": expected}
            response = {"changed": True, "declaration": payload, "extra": "preserved"}
            result, request = self.invoke(client.cmd_ticket_parent, payload, response)
            request.assert_called_once_with("PUT", "/api/context/tickets/parent", payload)
            self.assertEqual(result, response)

    def test_parent_dryrun_has_no_network_and_same_validated_payload(self):
        for parent, expected, operation in ((self.PARENT, None, "set"),
                                            (self.PARENT, self.PARENT, "reparent"),
                                            (None, self.PARENT, "remove")):
            payload = self.parent_payload() | {"parent": parent, "expectedParent": expected}
            result, request = self.invoke(client.cmd_ticket_parent, payload, dryrun=True)
            request.assert_not_called()
            self.assertEqual(result["request"], payload)
            self.assertEqual(result["operation"], operation)
            self.assertIn("unverified", result["validation"])

    def test_parent_guards_before_transport(self):
        valid = self.parent_payload()
        invalid = [[], None, valid | {"recordedAt": "server-owned"}]
        invalid += [{k: v for k, v in valid.items() if k != missing}
                    for missing in ("child", "parent", "expectedParent", "reason", "source")]
        invalid += [valid | {field: value} for field, value in (
            ("child", None), ("parent", {}), ("expectedParent", "unknown"),
            ("parent", self.CHILD), ("reason", " "), ("source", 3),
            ("observedAt", "yesterday"), ("observedAt", True),
            ("observedAt", "2026-09-14T12:00:00"),
            ("child", {"provider": "github", "key": "42", "url": "ignored?"}),
        )]
        for payload in invalid:
            with self.subTest(payload=payload):
                self.assert_rejected(client.cmd_ticket_parent, payload)

    def test_ticket_paths_preserves_full_empty_and_capped_disclosure(self):
        for paths in ([], [{"depth": 1, "hops": [{"parent": self.PARENT, "child": self.CHILD,
                       "reason": "Declared", "source": "User", "observedAt": None,
                       "recordedAt": "2026-09-14T12:00:00Z"}]}]):
            for capped in (False, True):
                payload = {"anchor": self.PARENT, "maxDepth": 2}
                response = {"paths": paths, "items": [{"uuid": "anchor-memory", "version": 1}],
                            "disclosure": {"maxDepth": 2, "pathLimit": 50, "memoryLimit": 50,
                                           "depthLimitReached": capped, "pathLimitReached": capped,
                                           "memoryLimitReached": capped,
                                           "hierarchyCoverage": "Undeclared upstream hierarchy was not followed; upstream freshness is unverified."},
                            "futureDisclosure": {"preserve": True}}
                result, request = self.invoke(client.cmd_ticket_paths, payload, response)
                self.assertEqual(result, response)
                request.assert_called_once_with("POST", "/api/context/tickets/paths",
                                                payload | {"direction": "outbound", "pathLimit": 50, "memoryLimit": 50})

    def test_identity_limits_and_nul_guards_apply_to_both_commands_and_dryrun(self):
        for field in ("child", "parent", "expectedParent", "anchor"):
            for key in ("provider", "key"):
                for value in ("x" * 513, "\U0001f600" * 257, "x\0", "\ud800", "\udfff"):
                    command = client.cmd_ticket_paths if field == "anchor" else client.cmd_ticket_parent
                    payload = ({"anchor": self.PARENT, "maxDepth": 1} if field == "anchor"
                               else self.parent_payload())
                    payload[field] = self.PARENT | {key: value}
                    for dryrun in ((False,) if field == "anchor" else (False, True)):
                        with self.subTest(field=field, key=key, value=repr(value[:12]), dryrun=dryrun):
                            self.assert_rejected(command, payload, dryrun=dryrun)

    def test_provenance_string_guards_apply_to_write_and_dryrun(self):
        for field in ("reason", "source"):
            for value in ("x" * 4001, "\U0001f600" * 2001, "x\0", "\ud800", None, True, " "):
                for dryrun in (False, True):
                    with self.subTest(field=field, value=repr(value)[:40], dryrun=dryrun):
                        self.assert_rejected(client.cmd_ticket_parent,
                                             self.parent_payload() | {field: value}, dryrun=dryrun)

    def test_utf16_boundary_strings_are_preserved_without_normalization(self):
        for identity, provenance in ((" x" + "y" * 510, "r" * 4000),
                                     ("\U0001f600" * 256, "\U0001f600" * 2000)):
            payload = self.parent_payload() | {
                "child": {"provider": identity, "key": identity},
                "parent": {"provider": identity, "key": "parent"},
                "expectedParent": {"provider": identity, "key": "old"},
                "reason": provenance, "source": provenance,
            }
            for dryrun in (False, True):
                result, request = self.invoke(client.cmd_ticket_parent, payload, {"changed": True}, dryrun)
                if dryrun:
                    self.assertEqual(result["request"], payload)
                    request.assert_not_called()
                else:
                    request.assert_called_once_with("PUT", "/api/context/tickets/parent", payload)
            query = {"anchor": payload["child"], "maxDepth": 1}
            _, request = self.invoke(client.cmd_ticket_paths, query, {})
            self.assertEqual(request.call_args.args[2]["anchor"], payload["child"])

    def test_ticket_path_filter_string_limits(self):
        for field, limit in (("scopeDimension", 32), ("kind", 64)):
            for value in ("x" * (limit + 1), "\U0001f600" * (limit // 2 + 1), "x\0", "\ud800"):
                self.assert_rejected(client.cmd_ticket_paths,
                                     {"anchor": self.PARENT, "maxDepth": 1, field: value})
            for value in ("x" * limit, "\U0001f600" * (limit // 2), None):
                _, request = self.invoke(client.cmd_ticket_paths,
                                         {"anchor": self.PARENT, "maxDepth": 1, field: value}, {})
                self.assertEqual(request.call_args.args[2][field], value)

    def test_observed_at_wire_shape_and_calendar_guards(self):
        invalid = ("2026-09-14X12:00:00+00:00", "2026-09-14 12:00:00Z",
                   "2026-09-14t12:00:00z", "20260914T120000Z", "2026-W38-1T12:00:00Z",
                   "2026-09-14T12:00:00+0000", "2026-09-14T12:00:00+00:00:30",
                   "2026-09-14T12:00:00+14:01", "2026-09-14T12:00:00-15:00",
                   "2026-09-14T12:00:00+01:60", "2026-09-14T12:00:00,5Z",
                   "2026-09-14T12:00:00.Z", "2026-09-14T12:00:00.12345678901234567Z",
                   "2026-02-29T12:00:00Z", "2026-09-14T24:00:00Z", "2026-09-14T12:00:60Z",
                   "0001-01-01T00:00:00+00:01", "9999-12-31T23:59:59-00:01",
                   "2026-09-14T12:00:00", True, 123, [])
        for value in invalid:
            for dryrun in (False, True):
                with self.subTest(value=value, dryrun=dryrun):
                    self.assert_rejected(client.cmd_ticket_parent,
                                         self.parent_payload() | {"observedAt": value}, dryrun=dryrun)
        for value in (None, "2026-09-14T12:00Z", "2026-09-14T12:00:00Z",
                      "2024-02-29T12:00:00.1234567+14:00", "2026-09-14T12:00:00-14:00",
                      "2026-09-14T12:00:00.1234567890123456+00:00",
                      "0001-01-01T00:00:00Z", "9999-12-31T23:59:59.9999999Z"):
            for dryrun in (False, True):
                payload = self.parent_payload() | {"observedAt": value}
                result, request = self.invoke(client.cmd_ticket_parent, payload, {}, dryrun)
                actual = result["request"] if dryrun else request.call_args.args[2]
                self.assertEqual(actual, payload)

    def test_ticket_paths_explicit_filters_and_boundaries(self):
        for direction in ("outbound", "inbound", "either"):
            for depth in (1, 5):
                payload = {"anchor": self.CHILD, "maxDepth": depth, "direction": direction,
                           "scopeDimension": "program", "kind": "decision", "pathLimit": 1, "memoryLimit": 200}
                _, request = self.invoke(client.cmd_ticket_paths, payload, {})
                request.assert_called_once_with("POST", "/api/context/tickets/paths", payload)

    def test_ticket_paths_guards_before_transport(self):
        valid = {"anchor": self.PARENT, "maxDepth": 2}
        invalid = [[], None, {"anchor": self.PARENT}, {"maxDepth": 2}, valid | {"scope": "program"}]
        invalid += [valid | {"maxDepth": value} for value in (None, 0, -1, 6, True, "2", 2.0)]
        invalid += [valid | {"anchor": value} for value in (None, {}, "github:10", {"provider": " ", "key": "10"})]
        invalid += [valid | {field: value} for field in ("pathLimit", "memoryLimit")
                    for value in (0, -1, 201, True, "50", None)]
        invalid += [valid | {"direction": value} for value in (None, "both", True, [])]
        invalid += [valid | {field: value} for field in ("scopeDimension", "kind") for value in (" ", [], 3)]
        for payload in invalid:
            with self.subTest(payload=payload):
                self.assert_rejected(client.cmd_ticket_paths, payload)

    def test_errors_propagate_without_retry_or_scope_change(self):
        for command, payload in ((client.cmd_ticket_paths, {"anchor": self.PARENT, "maxDepth": 2}),
                                 (client.cmd_ticket_parent, self.parent_payload())):
            for status in (403, 404, 409, 503):
                with patch.object(client, "read_payload", return_value=copy.deepcopy(payload)), \
                        patch.object(client, "_request", side_effect=client.ClientError(status, "error", "generic")) as request:
                    with self.assertRaises(client.ClientError) as error:
                        command(SimpleNamespace(payload=None, dryrun=False))
                    self.assertEqual(error.exception.status, status)
                    request.assert_called_once()

    def test_cli_routes_stdin_commands(self):
        for command, payload, method, endpoint in (
            ("ticket-parent", self.parent_payload(), "PUT", "/api/context/tickets/parent"),
            ("ticket-paths", {"anchor": self.PARENT, "maxDepth": 1}, "POST", "/api/context/tickets/paths"),
        ):
            with patch.object(sys, "argv", ["client", command]), \
                    patch.object(sys, "stdin", io.StringIO(json.dumps(payload))), \
                    patch.object(client, "_request", return_value={"disclosure": "kept"}) as request, \
                    redirect_stdout(io.StringIO()) as output:
                client.main()
            self.assertEqual(request.call_args.args[:2], (method, endpoint))
            self.assertEqual(json.loads(output.getvalue()), {"disclosure": "kept"})

    def test_http_wire_methods_json_and_response(self):
        for command, payload, method, endpoint in (
            (client.cmd_ticket_parent, self.parent_payload(), "PUT", "/api/context/tickets/parent"),
            (client.cmd_ticket_paths, {"anchor": self.PARENT, "maxDepth": 1,
                                      "direction": "outbound", "pathLimit": 50, "memoryLimit": 50},
             "POST", "/api/context/tickets/paths"),
        ):
            with patch.object(client, "read_payload", return_value=copy.deepcopy(payload)), \
                patch.object(client, "base_url", return_value="http://example.invalid"), \
                     patch.dict(os.environ, {client.ENV_READ_TOKEN: "read-token",
                                             client.ENV_WRITE_TOKEN: "write-token"}), \
                     patch.object(client, "_open") as transport, \
                    redirect_stdout(io.StringIO()) as output:
                transport.return_value.__enter__.return_value.read.return_value = b'{"disclosure":{"kept":true}}'
                command(SimpleNamespace(payload=None, dryrun=False))
            request = transport.call_args.args[0]
            self.assertEqual(request.full_url, "http://example.invalid" + endpoint)
            self.assertEqual(request.method, method)
            self.assertEqual(json.loads(request.data), payload)
            self.assertEqual(request.get_header("Content-type"), "application/json")
            expected_token = "write-token" if method == "PUT" else "read-token"
            self.assertEqual(request.get_header("Authorization"), f"Bearer {expected_token}")
            self.assertEqual(json.loads(output.getvalue()), {"disclosure": {"kept": True}})


class NearMissTagsTests(unittest.TestCase):
    def setUp(self):
        fixture = json.loads((HERE / "fixtures" / "near_miss_tags.json").read_text(encoding="utf-8"))
        self.evidence = fixture["evidence"]
        self.cases = fixture["cases"]

    def test_positive_and_negative_fixtures(self):
        for case in self.cases:
            with self.subTest(case=case["name"]):
                payload = copy.deepcopy(self.evidence)
                payload["records"][0]["tags"] = case["tags"]
                if case["relevant"] is None:
                    payload["analyses"] = []
                else:
                    payload["analyses"][0]["relevant"] = case["relevant"]
                original = copy.deepcopy(payload)
                report = near_miss.build_report(payload)
                self.assertEqual(payload, original)
                self.assertEqual(len(report["findings"]), case["findings"])
                self.assertEqual(report["selected"], payload["selected"])
                self.assertEqual(report["disclosure"], payload["disclosure"])
                for finding in report["findings"]:
                    self.assertEqual(finding["category"], "near-miss-tag")
                    self.assertEqual(finding["scope"], payload["scope"]["name"])
                    self.assertEqual(finding["memory"], payload["scope"]["records"][0] | {"status": "approved"})
                    self.assertFalse(finding["proposedEvidence"])
                    self.assertEqual(finding["classification"], "analysis")
                    self.assertEqual(finding["observation"]["classification"], "observation")
                    self.assertFalse(finding["observation"]["exactTagMatch"])
                    self.assertEqual(finding["basis"], payload["analyses"][0]["basis"])

    def test_all_containment_and_exact_case_not_synonyms(self):
        payload = self.evidence
        payload["originalQuery"]["tags"] = ["postgres", "database"]
        self.assertEqual(near_miss.build_report(payload)["findings"], [])
        payload["originalQuery"]["facetMatchMode"] = "all"
        self.assertEqual(len(near_miss.build_report(payload)["findings"]), 1)
        payload["records"][0]["tags"] = ["postgres", "database"]
        self.assertEqual(near_miss.build_report(payload)["findings"], [])
        payload["records"][0]["tags"] = ["Postgres", "database"]
        self.assertEqual(len(near_miss.build_report(payload)["findings"]), 1)

    def test_original_api_query_is_the_only_tag_predicate_source(self):
        query = {"query": "PostgreSQL", "tags": ["postgres", "database"], "facets": ["storage"],
                 "kind": "decision", "status": "approved", "scopeDimension": "product",
                 "groupUuid": None, "ticketProvider": None, "ticketKey": None, "repo": "example",
                 "initiativeName": "example", "includeProposed": False, "currentOnly": True,
                 "asOf": None, "limit": 10}
        for mode, count in ((None, 0), ("any", 0), ("all", 1)):
            payload = copy.deepcopy(self.evidence)
            payload["originalQuery"] = query.copy()
            if mode is not None:
                payload["originalQuery"]["facetMatchMode"] = mode
            original = copy.deepcopy(payload)
            report = near_miss.build_report(payload)
            self.assertEqual(payload, original)
            self.assertEqual(report["originalQuery"], original["originalQuery"])
            self.assertEqual(len(report["findings"]), count)
            if count:
                self.assertEqual(report["findings"][0]["observation"]["facetMatchMode"], mode)
        for mode in (None, "ALL", True, 1, [], {}):
            payload = copy.deepcopy(self.evidence)
            payload["originalQuery"]["facetMatchMode"] = mode
            with self.subTest(mode=mode), self.assertRaises(ValueError):
                near_miss.build_report(payload)
        for field, value in (("tagsMatchMode", "all"), ("nonTagFilters", {"facetMatchMode": "all"}),
                             ("selectedTags", ["database"]), ("facetMatchMod", "all")):
            for location in ("originalQuery", "input"):
                payload = copy.deepcopy(self.evidence)
                (payload if location == "input" else payload[location])[field] = value
                with self.subTest(field=field, location=location), self.assertRaises(ValueError):
                    near_miss.build_report(payload)
        payload = copy.deepcopy(self.evidence)
        payload["criteria"] = payload.pop("originalQuery")
        with self.assertRaises(ValueError):
            near_miss.build_report(payload)

    def test_mixed_scope_and_status_evidence_is_retained_not_promoted_or_dropped(self):
        payload = self.evidence
        payload["scope"]["name"] = "Explicitly supplied mixed-scope evidence"
        payload["records"] = []
        payload["analyses"] = []
        payload["scope"]["records"] = []
        for version, (dimension, identifier, status) in enumerate((
            ("product", None, "approved"), ("customer", "customer-a", "proposed"),
            ("program", "initiative-a", "proposed"), ("self", "practitioner", "approved"),
        ), 1):
            ref = {"uuid": "aaaaaaaa-0000-4000-8000-000000000001", "version": version}
            scoped = ref | {"scopeDimension": dimension, "scopeIdentifier": identifier}
            payload["scope"]["records"].append(scoped)
            payload["records"].append(scoped | {"status": status, "tags": ["database"],
                                                   "statement": "Uses PostgreSQL."})
            payload["analyses"].append(ref | {"relevant": True, "basis": {
                "classification": "analysis", "author": "caller", "quote": "Uses PostgreSQL.",
                "explanation": "Database evidence relevant only within its stated applicability and lifecycle."}})
        original = copy.deepcopy(payload)
        report = near_miss.build_report(payload)
        self.assertEqual(payload, original)
        self.assertEqual(len(report["findings"]), 4)
        self.assertEqual(report["selected"], [])
        for record, finding in zip(payload["records"], report["findings"]):
            self.assertEqual(finding["memory"], {k: record[k] for k in
                             ("uuid", "version", "status", "scopeDimension", "scopeIdentifier")})
            self.assertEqual(finding["proposedEvidence"], record["status"] == "proposed")
        self.assertIn("not canon", report["qualification"])
        self.assertIn("not the examined-set name or query scope", report["qualification"])

    def test_record_scope_must_match_approved_reference_not_query_or_set_name(self):
        for dimension, identifier in (("program", "initiative"), ("customer", "customer-a"),
                                      ("product", "other-product")):
            payload = copy.deepcopy(self.evidence)
            payload["records"][0].update(scopeDimension=dimension, scopeIdentifier=identifier)
            payload["originalQuery"]["scopeDimension"] = dimension
            payload["scope"]["name"] = f"Approved {dimension} {identifier}"
            with self.subTest(dimension=dimension), self.assertRaises(ValueError):
                near_miss.build_report(payload)
        for field in ("records", "analyses", "selected"):
            payload = copy.deepcopy(self.evidence)
            if field == "selected":
                payload[field] = [{"uuid": payload["records"][0]["uuid"], "version": 2}]
            else:
                payload[field][0]["version"] = 2
            with self.subTest(field=field), self.assertRaises(ValueError):
                near_miss.build_report(payload)

    def test_empty_evidence_is_not_absence_proof(self):
        payload = self.evidence | {"records": [], "analyses": []}
        payload["scope"]["records"] = []
        report = near_miss.build_report(payload)
        self.assertEqual(report["findings"], [])
        for text in ("supplied, approved examined evidence", "not store-wide absence", "non-tag filters", "Tag relationships were not followed"):
            self.assertIn(text, report["qualification"])

    def test_findings_order_is_deterministic_and_selected_membership_unchanged(self):
        payload = self.evidence
        for version in (3, 2):
            payload["scope"]["records"].append(payload["scope"]["records"][0] | {"version": version})
            payload["records"].append(payload["records"][0] | {"version": version})
            payload["analyses"].append(payload["analyses"][0] | {"version": version})
        payload["selected"] = [{"uuid": r["uuid"], "version": r["version"]} for r in payload["records"]]
        report = near_miss.build_report(payload)
        self.assertEqual([f["memory"]["version"] for f in report["findings"]], [1, 2, 3])
        payload["analyses"].reverse()
        payload["records"].reverse()
        self.assertEqual(near_miss.build_report(payload), report)
        self.assertEqual(report["selected"], payload["selected"])

    def test_invalid_basis_identity_scope_and_bounds_rejected(self):
        changes = [
            (("analyses", 0, "basis", "classification"), "observation"),
            (("analyses", 0, "basis", "author"), "heuristic"),
            (("analyses", 0, "basis", "explanation"), " "),
            (("analyses", 0, "basis", "quote"), "Guessed unseen claim"),
            (("analyses", 0, "basis", "quote"), ""),
            (("analyses", 0, "relevant"), "true"),
            (("analyses", 0, "uuid"), "aaaaaaaa-0000-4000-8000-000000000002"),
            (("analyses", 0, "version"), 2),
            (("records", 0, "uuid"), "not-a-uuid"),
            (("records", 0, "uuid"), "00000000-0000-0000-0000-000000000000"),
            (("records", 0, "scopeDimension"), "hidden-program"),
            (("records", 0, "status"), "settled"),
            (("records", 0, "status"), None),
            (("records", 0, "scopeIdentifier"), []),
            (("records", 0, "scopeIdentifier"), " "),
            (("scope", "records", 0, "scopeDimension"), "program"),
            (("scope", "records", 0, "scopeIdentifier"), 3),
            (("scope", "records", 0, "scopeIdentifier"), "x" * 201),
            (("records", 0, "version"), True),
            (("records", 0, "version"), 0),
            (("records", 0, "tags"), "database"),
            (("records", 0, "tags"), ["database"] * 201),
            (("scope", "records"), []),
            (("scope", "authorization"), "inferred"),
            (("originalQuery", "tags"), []),
            (("originalQuery", "facetMatchMode"), "synonyms"),
            (("records",), self.evidence["records"] * 201),
            (("analyses",), self.evidence["analyses"] * 201),
            (("records", 0, "statement"), "x" * 8001),
            (("selected",), [{"uuid": "aaaaaaaa-0000-4000-8000-000000000002", "version": 1}]),
        ]
        for path, value in changes:
            with self.subTest(path=path, value=str(value)[:80]):
                payload = copy.deepcopy(self.evidence)
                target = payload
                for key in path[:-1]:
                    target = target[key]
                target[path[-1]] = value
                with self.assertRaises(ValueError):
                    near_miss.build_report(payload)
        with self.assertRaises(ValueError):
            near_miss.build_report(self.evidence | {"globalVocabulary": ["telemetry"]})
        for field in ("records", "analyses", "selected", "disclosure", "scope", "originalQuery"):
            payload = copy.deepcopy(self.evidence)
            del payload[field]
            with self.assertRaises(ValueError):
                near_miss.build_report(payload)
        for field in ("status", "scopeDimension", "scopeIdentifier"):
            payload = copy.deepcopy(self.evidence)
            del payload["records"][0][field]
            with self.subTest(missing=field), self.assertRaises(ValueError):
                near_miss.build_report(payload)

    def test_no_network_file_access_or_writes_and_executable_output(self):
        raw = json.dumps(self.evidence).encode("utf-8")
        with patch("builtins.open", side_effect=AssertionError("file access")) as files, \
                patch("os.open", side_effect=AssertionError("file access")) as os_files, \
                patch("socket.socket", side_effect=AssertionError("network")) as sockets, \
                patch.object(client, "_request", side_effect=AssertionError("store access")) as requests, \
                patch.object(sys, "stdin", SimpleNamespace(buffer=io.BytesIO(raw))), \
                redirect_stdout(io.StringIO()) as output:
            self.assertEqual(near_miss.main(), 0)
        for mock in (files, os_files, sockets, requests):
            mock.assert_not_called()
        self.assertEqual(json.loads(output.getvalue()), near_miss.build_report(self.evidence))

    def test_cli_rejects_oversize_or_malformed_input_without_partial_report(self):
        invalid_evidence = []
        for field, value in (("status", "unknown"), ("scopeIdentifier", "not-approved")):
            payload = copy.deepcopy(self.evidence)
            payload["records"][0][field] = value
            invalid_evidence.append(json.dumps(payload).encode("utf-8"))
        payload = copy.deepcopy(self.evidence)
        payload["originalQuery"]["tagsMatchMode"] = "all"
        invalid_evidence.append(json.dumps(payload).encode("utf-8"))
        for raw in [b"x" * (near_miss.MAX_BYTES + 1), b"{bad-json", b"[]", b"null", *invalid_evidence]:
            with patch.object(sys, "stdin", SimpleNamespace(buffer=io.BytesIO(raw))), \
                    redirect_stdout(io.StringIO()) as output, redirect_stderr(io.StringIO()) as error:
                self.assertEqual(near_miss.main(), 1)
            self.assertEqual(output.getvalue(), "")
            self.assertIn("Invalid near-miss evidence", error.getvalue())


if __name__ == "__main__":
    unittest.main(verbosity=2)
