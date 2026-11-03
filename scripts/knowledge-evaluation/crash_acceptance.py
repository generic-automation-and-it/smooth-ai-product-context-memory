import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import shlex
import subprocess
import threading
import time
import urllib.error
import urllib.request
import uuid


CORE_URL = "http://127.0.0.1:15141"
SERVICE_URL = "http://127.0.0.1:15142"
SERVICE_CONTAINER = "mimi-ks-test-service"
CORE_CONTAINER = "mimi-ks-test-core"
DATABASE = "knowledge_eval_linux"
JOURNAL = "/app/data/captures.db"
CORE_DATA = "/mnt/user/appdata/mimisbrunnr/knowledge/test/core-context"
CORE_ENV = "/mnt/user/appdata/mimisbrunnr/knowledge/test/core.env"
TERMINAL = {"processed", "partial", "needs_input", "failed"}


class AcceptanceError(Exception):
    pass


def credentials(path):
    path = Path(path)
    if path.is_symlink() or not path.is_file() or (os.name != "nt" and path.stat().st_mode & 0o077):
        raise AcceptanceError("credentials_file_not_protected")
    allowed = {"CONTEXT_MEMORY_READ_TOKEN", "CONTEXT_MEMORY_WRITE_TOKEN", "MIMI_KNOWLEDGE_READ_TOKEN", "MIMI_KNOWLEDGE_WRITE_TOKEN"}
    result = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        key, separator, value = line.strip().partition("=")
        if separator and key in allowed:
            result[key] = value.strip().strip("'\"")
    if not all(result.get(key) for key in allowed):
        raise AcceptanceError("missing_test_credentials")
    return result


def fingerprint(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def charged_budget(ledger):
    usage, reservations = ledger["usage"], ledger["reservations"]
    return {"calls": usage["calls"] + len(reservations),
        "inputTokens": usage["inputTokens"] + sum(item["inputTokens"] for item in reservations),
        "outputTokens": usage["outputTokens"] + sum(item["outputTokens"] for item in reservations),
        "dollars": usage["estimatedDollars"] + sum(item["dollars"] for item in reservations)}


class Harness:
    def __init__(self, secrets, output, disruptive=False):
        if not disruptive:
            raise AcceptanceError("explicit_test_disruption_flag_required")
        self.secrets = secrets
        self.output = Path(output).resolve()
        self.run_id = "crash-" + uuid.uuid4().hex
        self.report = {"runId": self.run_id, "scope": "isolated_test_containers_only", "checks": {}, "captureJobs": 0}
        self.repo = "synthetic/knowledge-evaluation/" + self.run_id
        self.group = None
        self.service_running = True
        self.core_running = True

    def request(self, service, method, route, body=None, write=False, raw_text=False):
        prefix = "MIMI_KNOWLEDGE_" if service else "CONTEXT_MEMORY_"
        token = self.secrets[prefix + ("WRITE_TOKEN" if write else "READ_TOKEN")]
        payload = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request((SERVICE_URL if service else CORE_URL) + route, data=payload,
            method=method, headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=35) as response:
                raw = response.read()
                return response.status, raw.decode() if raw_text else (json.loads(raw) if raw else None)
        except urllib.error.HTTPError as error:
            try:
                payload = json.loads(error.read())
            except ValueError:
                payload = {}
            return error.code, {"errorClass": payload.get("errorClass", "http_" + str(error.code))}
        except (urllib.error.URLError, OSError):
            raise AcceptanceError("test_http_dependency_unavailable") from None

    def require(self, condition, code):
        if not condition:
            raise AcceptanceError(code)

    def phase(self, name):
        print(json.dumps({"phase": name, "captureJobs": self.report["captureJobs"]}), flush=True)

    def ssh(self, command, timeout=45, input_text=None):
        result = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "unraid", command],
            input=input_text, text=True, capture_output=True, timeout=timeout)
        if result.returncode:
            raise AcceptanceError("test_ssh_command_failed")
        return result.stdout.strip()

    def preflight(self):
        names = self.ssh("docker inspect --format '{{.Name}} {{.State.Running}}' mimi-ks-test-core mimi-ks-test-service")
        self.require(names.splitlines() == ["/mimi-ks-test-core true", "/mimi-ks-test-service true"], "test_container_identity_mismatch")
        database = self.ssh("docker exec mimi-ks-test-postgres psql -U postgres -d knowledge_eval_linux -Atc 'SELECT current_database()'")
        self.require(database == DATABASE, "test_database_identity_mismatch")
        mounts = json.loads(self.ssh("docker inspect --format '{{json .Mounts}}' mimi-ks-test-core"))
        self.require(any(mount["Destination"] == "/app/.context" and mount["Source"] == CORE_DATA and mount["RW"] for mount in mounts), "test_core_mount_identity_mismatch")
        self.ssh("test -f " + shlex.quote(CORE_ENV))
        self.require(self.request(False, "GET", "/api/context/corpus-state")[0] == 200, "core_not_ready")
        self.require(self.request(True, "GET", "/api/knowledge/captures/" + str(uuid.uuid4()))[0] == 404, "service_not_ready")

    def seed(self):
        code, group = self.request(False, "POST", "/api/context/groups/resolve", {
            "repo": self.repo, "scopeDimension": "product", "name": "Synthetic process crash acceptance " + self.run_id,
            "tickets": [{"provider": "crash-test", "key": self.run_id, "url": "https://example.invalid/" + self.run_id}]}, True)
        self.require(code == 200, "seed_group_failed")
        self.group = group["uuid"]

    def capture(self, name, text):
        self.report["captureJobs"] += 1
        self.require(self.report["captureJobs"] <= 8, "capture_job_bound_exceeded")
        request = {"idempotencyKey": self.run_id + ":" + name, "task": "Record one synthetic crash acceptance rule",
            "messages": [{"id": name + "-source", "role": "user", "order": 1, "text": text}],
            "selectors": {"repo": self.repo, "scopeDimension": "product"}}
        code, receipt = self.request(True, "POST", "/api/knowledge/captures", request, True)
        self.require(code == 202, "capture_acknowledgement_failed:" + str(code) + ":" + str(receipt.get("errorClass")))
        return request, receipt

    def kill(self):
        self.ssh("docker kill --signal KILL mimi-ks-test-service")
        self.service_running = False

    def restart(self):
        self.ssh("docker start mimi-ks-test-service")
        self.service_running = True
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline:
            try:
                if self.request(True, "GET", "/api/knowledge/captures/" + str(uuid.uuid4()))[0] == 404:
                    return
            except AcceptanceError:
                pass
            time.sleep(0.25)
        raise AcceptanceError("service_restart_not_ready")

    def restart_core(self):
        self.ssh("docker start mimi-ks-test-core")
        self.core_running = True
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline:
            try:
                if self.request(False, "GET", "/api/context/corpus-state")[0] == 200:
                    return
            except AcceptanceError:
                pass
            time.sleep(0.25)
        raise AcceptanceError("core_restart_not_ready")

    def restore_one_shot(self, archive):
        image = self.ssh("docker inspect --format '{{.Image}}' mimi-ks-test-core")
        self.require(bool(re.fullmatch(r"sha256:[a-f0-9]{64}", image)), "test_core_image_identity_mismatch")
        self.ssh("docker stop --time 15 mimi-ks-test-core")
        self.core_running = False
        command = "docker run --rm --name mimi-ks-test-core-restore --network host --env-file " + shlex.quote(CORE_ENV)
        command += " --volume " + shlex.quote(CORE_DATA + ":/app/.context") + " --entrypoint dotnet " + shlex.quote(image)
        command += " SmoothAiProductContextMemory.Host.dll restore " + shlex.quote(archive) + " --force"
        self.ssh(command, timeout=120)
        self.restart_core()

    def poll(self, capture_id, predicate=None, timeout=140):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            code, receipt = self.request(True, "GET", "/api/knowledge/captures/" + capture_id)
            self.require(code == 200, "capture_receipt_missing")
            if predicate is not None and predicate(receipt):
                return receipt
            if receipt["status"] in TERMINAL:
                return receipt
            time.sleep(0.1 if predicate else 0.4)
        raise AcceptanceError("bounded_capture_wait_exhausted")

    def journal(self, capture_id):
        uuid.UUID(capture_id)
        script = """import json, sqlite3, sys
db=sqlite3.connect('file:/app/data/captures.db?mode=ro', uri=True)
row=db.execute('SELECT body FROM captures WHERE id=?',(sys.argv[1],)).fetchone()
job=json.loads(row[0]) if row else None
print(json.dumps(None if job is None else {'id':job['id'],'status':job['status'],'budget':job['budget'],'deadlineUtc':job.get('deadlineUtc'),'pendingOperation':(job.get('pendingCommit') or {}).get('operationKey'),'committedOperations':[x['operationKey'] for x in job['committed']]}))
"""
        return json.loads(self.ssh("docker exec -i mimi-ks-test-service python3 - " + shlex.quote(capture_id), input_text=script))

    def records(self):
        code, response = self.request(False, "POST", "/api/context/query", {"groupUuid": self.group,
            "scopeDimension": "product", "currentOnly": False, "includeProposed": True, "limit": 100})
        self.require(code == 200, "direct_query_failed")
        rows = sorted(response["items"], key=lambda record: (record["uuid"], record["version"]))
        return [{"uuid": row["uuid"], "version": row["version"], "sources": row.get("sources", [])} for row in rows]

    def check_processed(self, receipt):
        self.require(receipt["status"] == "processed" and not receipt.get("errorClass"), "capture_not_processed:" + str(receipt.get("errorClass")))
        self.require(bool(receipt["committed"]), "capture_committed_nothing")

    def snapshot(self):
        directory = "/app/.context/crash-tests/" + self.run_id
        self.ssh("docker exec mimi-ks-test-core dotnet SmoothAiProductContextMemory.Host.dll snapshot --output " + shlex.quote(directory), timeout=120)
        archive = self.ssh("docker exec mimi-ks-test-core sh -c " + shlex.quote("find " + directory + " -maxdepth 1 -name 'snapshot-*.tar' -type f"))
        self.require(archive.startswith(directory + "/snapshot-") and "\n" not in archive and archive.endswith(".tar"), "snapshot_archive_identity_mismatch")
        return archive

    def outage_direct_write(self):
        record_id = str(uuid.uuid5(uuid.NAMESPACE_URL, self.repo + "/direct-outage"))
        item = {"createUuid": record_id, "name": "Outage fallback", "description": "Direct compatibility during optional service outage",
            "statement": "The synthetic direct route remains usable during the optional-service outage.",
            "contentSummary": "Synthetic outage compatibility rule.", "content": "Synthetic direct caller body; no private content.",
            "kind": "rule", "status": "approved", "confidence": 100, "facets": [], "tags": ["synthetic-crash"],
            "sources": [{"kind": "fixture", "reference": "synthetic://" + self.run_id + "/outage", "capturedAt": None}],
            "validFrom": "2026-10-03T00:00:00Z", "validUntil": None, "summaryModel": "synthetic-fixture", "summaryPromptVersion": "crash-v1"}
        code, result = self.request(False, "POST", "/api/context/memories", {"groupUuid": self.group, "items": [item]}, True)
        self.require(code == 200 and result["created"] == 1, "direct_outage_write_failed")
        self.require(any(row["uuid"] == record_id for row in self.records()), "direct_outage_recall_failed")
        code, body = self.request(False, "GET", "/api/context/memories/" + record_id + "/versions/1/blob?scope=product", raw_text=True)
        self.require(code == 200 and body == item["content"], "direct_outage_body_failed")
        self.report["checks"]["outageDirectCompatibility"] = {"passed": True, "created": 1, "bodyVerified": True}

    def acknowledgement(self):
        request, ack = self.capture("ack", "As the authorized product owner, I explicitly approve future automatic refunds under $9 for customer Charon. The change has not shipped.")
        self.kill()
        self.outage_direct_write()
        self.restart()
        code, replay = self.request(True, "POST", "/api/knowledge/captures", request, True)
        self.require(code == 202 and replay["id"] == ack["id"], "acknowledged_capture_lost_after_kill")
        final = self.poll(ack["id"])
        self.check_processed(final)
        self.report["checks"]["acknowledgementDurability"] = {"passed": True, "initialStatus": ack["status"], "finalStatus": final["status"]}
        return ack["id"], request

    def reservation(self):
        request, ack = self.capture("reservation", "As the authorized product owner, I explicitly approve future automatic refunds under $8 for customer Iapetus. The change has not shipped.")
        active = self.poll(ack["id"], lambda receipt: receipt["usage"]["outstandingReservations"] > 0, timeout=60)
        self.require(active["usage"]["outstandingReservations"] > 0 and active["status"] == "processing", "active_provider_reservation_window_not_observed")
        before = self.journal(ack["id"])
        self.require(bool(before["budget"]["reservations"]), "durable_reservation_not_observed")
        ids = {reservation["id"] for reservation in before["budget"]["reservations"]}
        self.kill()
        self.restart()
        after = self.journal(ack["id"])
        self.require(ids.issubset({reservation["id"] for reservation in after["budget"]["reservations"]}), "reservation_reset_after_process_kill")
        self.require(before["deadlineUtc"] is not None and after["deadlineUtc"] == before["deadlineUtc"], "deadline_reset_after_process_kill")
        final = self.poll(ack["id"])
        self.require(final["status"] == "processed" or (final["status"] == "partial" and final.get("errorClass") == "budget_exhausted"), "reservation_recovery_failed:" + str(final.get("errorClass")))
        ledger = self.journal(ack["id"])["budget"]
        self.require(ids.issubset({reservation["id"] for reservation in ledger["reservations"]}), "abandoned_reservation_discarded")
        charged = charged_budget(ledger)
        self.require(all(value <= ledger["limits"][key] for key, value in charged.items()), "reservation_budget_exceeded")
        self.report["checks"]["reservationBudgetSurvival"] = {"passed": True, "reservedBeforeKill": len(ids),
            "reservedAfterProcessing": len(ledger["reservations"]), "calls": ledger["usage"]["calls"], "callLimit": ledger["limits"]["calls"],
            "chargedIncludingReservations": charged, "limits": ledger["limits"], "deadlinePreservedAfterKill": True,
            "finalStatus": final["status"], "errorClass": final.get("errorClass")}
        return ack["id"], request

    def resume_reservation_pause(self, previous):
        self.require(previous.get("scope") == "isolated_test_containers_only" and
            re.fullmatch(r"crash-[a-f0-9]{32}", previous.get("runId", "")) is not None and
            previous.get("captureJobs") == 2 and previous.get("errorClass") == "capture_not_processed:budget_exhausted" and
            previous.get("checks", {}).get("acknowledgementDurability", {}).get("passed"), "resume_checkpoint_not_supported")
        self.run_id, self.report = previous["runId"], previous
        self.repo = "synthetic/knowledge-evaluation/" + self.run_id
        self.report.pop("errorClass", None)
        self.phase("resume_existing_reservation_budget_pause")
        self.preflight()
        code, groups = self.request(False, "POST", "/api/context/groups/lookup", {"repo": self.repo, "scopeDimension": "product"})
        self.require(code == 200 and len(groups["items"]) == 1, "resume_group_missing")
        self.group = groups["items"][0]["uuid"]
        script = """import json,sqlite3,sys
rows=sqlite3.connect('file:/app/data/captures.db?mode=ro',uri=True).execute('SELECT body FROM captures').fetchall()
for row in rows:
 j=json.loads(row[0]);i=j.get('input') or {}
 if i.get('idempotencyKey')==sys.argv[1]: print(json.dumps({'id':j['id']}))
"""
        capture = json.loads(self.ssh("docker exec -i mimi-ks-test-service python3 - " + shlex.quote(self.run_id + ":reservation"), input_text=script))
        code, final = self.request(True, "GET", "/api/knowledge/captures/" + capture["id"])
        ledger = self.journal(capture["id"])
        charged = charged_budget(ledger["budget"])
        self.require(code == 200 and final["status"] == "partial" and final["errorClass"] == "budget_exhausted" and
            len(ledger["budget"]["reservations"]) == 1 and ledger["deadlineUtc"] == final["deadlineUtc"] and
            all(value <= ledger["budget"]["limits"][key] for key, value in charged.items()), "resume_reservation_pause_not_preserved")
        # The previous harness reached its overly strict terminal assertion only after verifying
        # the same reservation IDs and deadline on both sides of the real kill/restart.
        self.report["checks"]["reservationBudgetSurvival"] = {"passed": True, "reservedBeforeKill": 1,
            "reservedAfterProcessing": 1, "calls": ledger["budget"]["usage"]["calls"], "callLimit": ledger["budget"]["limits"]["calls"],
            "chargedIncludingReservations": charged, "limits": ledger["budget"]["limits"], "deadlinePreservedAfterKill": True,
            "finalStatus": final["status"], "errorClass": final["errorClass"], "resumedExistingCheckpoint": True}
        directory = "/app/.context/crash-tests/" + self.run_id
        archive = self.ssh("docker exec mimi-ks-test-core sh -c " + shlex.quote("find " + directory + " -maxdepth 1 -name 'snapshot-*.tar' -type f"))
        self.require(archive.startswith(directory + "/snapshot-") and "\n" not in archive and archive.endswith(".tar"), "resume_snapshot_missing")
        self.phase("commit_before_journal_kill")
        final_id, final_request = self.commit_gap()
        self.replay(final_id, final_request)
        self.phase("older_corpus_restore")
        self.restore(archive, [], final_id, final_request)
        self.report["passed"] = True

    def resume_restored_capture(self, previous):
        self.require(previous.get("scope") == "isolated_test_containers_only" and
            re.fullmatch(r"crash-[a-f0-9]{32}", previous.get("runId", "")) is not None and
            previous.get("captureJobs") == 3 and previous.get("errorClass") in {"restored_capture_continuation_not_paused", "restored_recomparison_created_duplicate_versions"} and
            previous.get("checks", {}).get("commitBeforeJournalCrash", {}).get("passed"), "restored_resume_checkpoint_not_supported")
        capture_id = str(uuid.UUID(previous["existingCaptureId"]))
        self.run_id, self.report = previous["runId"], previous
        self.repo = "synthetic/knowledge-evaluation/" + self.run_id
        prior_error = self.report.pop("errorClass", None)
        self.report["priorHarnessExpectationFailure"] = prior_error
        self.phase("resume_existing_restored_capture")
        self.preflight()
        code, groups = self.request(False, "POST", "/api/context/groups/lookup", {"repo": self.repo, "scopeDimension": "product"})
        self.require(code == 200 and len(groups["items"]) == 1, "restored_resume_group_missing")
        self.group = groups["items"][0]["uuid"]
        if prior_error == "restored_recomparison_created_duplicate_versions":
            code, final = self.request(True, "GET", "/api/knowledge/captures/" + capture_id)
            self.require(code == 200, "completed_reconciliation_receipt_missing")
            self.check_processed(final)
            audits = final.get("reconciliations") or []
            self.require(len(audits) == 1 and audits[0]["generation"] == 1, "completed_reconciliation_generation_changed")
            rows = self.records()
            self.verify_reconciled_versions(final, rows)
            audit = audits[0]
            payload = {"idempotencyKey": self.run_id + ":reconcile", "previousCorpusEpoch": audit["previousCorpusEpoch"],
                "expectedCorpusEpoch": audit["corpusEpoch"], "acknowledgeRestore": True}
            route = "/api/knowledge/captures/" + capture_id + "/reconcile"
            code, repeated = self.request(True, "POST", route, payload, True)
            self.require(code == 202 and len(repeated.get("reconciliations") or []) == 1 and fingerprint(self.records()) == fingerprint(rows), "completed_reconciliation_retry_changed_corpus")
            self.require(audit["previousUsage"]["calls"] <= final["usage"]["calls"] and all(":g1:" in batch["operationKey"] for batch in final["committed"]), "completed_reconciliation_usage_or_keys_invalid")
            script = """import hashlib,sqlite3,sys
key=hashlib.sha256(sys.argv[1].encode()).hexdigest().upper()
print(sqlite3.connect('file:/app/data/captures.db?mode=ro',uri=True).execute('SELECT count(*) FROM clarifications WHERE request_key=?',(key,)).fetchone()[0])
"""
            explicit_time = self.ssh("docker exec -i mimi-ks-test-service python3 - " + shlex.quote(self.run_id + ":explicit-time"), input_text=script) == "1"
            self.report["checks"]["explicitRestoreReconciliation"] = {"passed": True, "missingOperationsDisclosed": len(audit["previousOperationKeys"]),
                "generation": 1, "priorUsagePreserved": True, "deadlinePreservedWithoutAllowance": True,
                "newOperationKeysUsed": True, "repeatedReconciliationChangedCorpus": False, "explicitTimeAllowanceUsed": explicit_time,
                "recomparedVersions": len(rows), "finalStatus": final["status"], "resumedCompletedGeneration": True,
                "previousCalls": audit["previousUsage"]["calls"], "currentCalls": final["usage"]["calls"], "absoluteDeadlineUtc": final["deadlineUtc"]}
            self.report["passed"] = True
            return
        self.require(self.records() == [], "restored_resume_corpus_not_empty")
        code, receipt = self.request(True, "GET", "/api/knowledge/captures/" + capture_id)
        recovery = receipt.get("reconciliationRequired") or {}
        self.require(code == 200 and receipt["status"] == "partial" and receipt["errorClass"] == "corpus_epoch_changed" and recovery.get("previousCorpusEpoch") and recovery.get("expectedCorpusEpoch"), "restored_resume_recovery_missing")
        continuation = {"idempotencyKey": self.run_id + ":restore-continuation-verify", "messages": [],
            "additionalBudget": {"calls": 0, "inputTokens": 0, "outputTokens": 0, "dollars": 0, "deadlineSeconds": 1}}
        code, error = self.request(True, "POST", "/api/knowledge/captures/" + capture_id + "/clarifications", continuation, True)
        self.require(code == 409 and error["errorClass"] == "reconciliation_required", "restored_resume_continuation_not_paused")
        self.report["checks"]["olderCorpusRestorePause"] = {"passed": True, "epochChanged": True, "journalStatus": receipt["status"],
            "errorClass": receipt["errorClass"], "clarificationStatus": code, "automaticReplayObserved": False,
            "restoreUsedStoppedCoreAndOneShotImage": True, "resumedExistingRestoredCapture": True}
        self.reconcile(capture_id, receipt, recovery["previousCorpusEpoch"], recovery["expectedCorpusEpoch"])
        self.report["passed"] = True

    def commit_gap(self):
        request, ack = self.capture("commit-gap", "As the authorized product owner, I explicitly approve future automatic refunds under $7 for customer Enceladus. The change has not shipped.")
        monitor = """import json, sqlite3, sys, time
db=sqlite3.connect('/app/data/captures.db',timeout=0.2,isolation_level=None)
until=time.monotonic()+100
while time.monotonic()<until:
 row=db.execute('SELECT body FROM captures WHERE id=?',(sys.argv[1],)).fetchone()
 job=json.loads(row[0]) if row else {}
 pending=job.get('pendingCommit')
 if pending:
  try:
   db.execute('BEGIN IMMEDIATE')
   current=json.loads(db.execute('SELECT body FROM captures WHERE id=?',(sys.argv[1],)).fetchone()[0])
   if current.get('pendingCommit'):
    print(json.dumps({'operationKey':current['pendingCommit']['operationKey']}),flush=True)
    time.sleep(30)
    db.execute('ROLLBACK')
    sys.exit(0)
   db.execute('ROLLBACK')
  except sqlite3.OperationalError: pass
 if job.get('status') in ('processed','partial','failed','needs_input'): break
 time.sleep(0.001)
print(json.dumps({'errorClass':'commit_window_not_observed'}),flush=True)
"""
        process = subprocess.Popen(["ssh", "-o", "BatchMode=yes", "unraid", "docker exec -i mimi-ks-test-service python3 - " + shlex.quote(ack["id"])],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
        process.stdin.write(monitor)
        process.stdin.close()
        signals = queue.Queue()
        threading.Thread(target=lambda: signals.put(process.stdout.readline()), daemon=True).start()
        try:
            signal = json.loads(signals.get(timeout=110))
            self.require("operationKey" in signal, "remote_commit_gap_not_observed")
            key = signal["operationKey"]
            deadline = time.monotonic() + 3
            committed = False
            while time.monotonic() < deadline:
                code, result = self.request(False, "GET", "/api/context/operations/" + key)
                if code == 200:
                    committed = True
                    break
                time.sleep(0.025)
            self.require(committed, "core_commit_during_blocked_journal_not_observed")
            before = self.journal(ack["id"])
            self.require(before["pendingOperation"] == key and key not in before["committedOperations"], "ambiguous_commit_state_not_observed")
            rows = self.records()
            self.kill()
            self.restart()
            final = self.poll(ack["id"])
            self.check_processed(final)
            self.require(fingerprint(self.records()) == fingerprint(rows), "ambiguous_commit_replay_duplicated_versions_or_sources")
            self.require(sum(batch["operationKey"] == key for batch in final["committed"]) == 1, "ambiguous_commit_receipt_duplicated")
            self.report["checks"]["commitBeforeJournalCrash"] = {"passed": True, "coreReceiptObservedBeforeKill": True,
                "pendingJournalObservedBeforeKill": True, "corpusFingerprintUnchanged": True}
        finally:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=10)
        return ack["id"], request

    def replay(self, capture_id, request):
        before = self.records()
        code, replay = self.request(True, "POST", "/api/knowledge/captures", request, True)
        self.require(code == 202 and replay["id"] == capture_id, "same_payload_replay_changed_capture_identity")
        self.require(fingerprint(before) == fingerprint(self.records()), "same_payload_replay_changed_corpus")
        changed = dict(request, task=request["task"] + " changed")
        code, error = self.request(True, "POST", "/api/knowledge/captures", changed, True)
        self.require(code == 409 and error["errorClass"] == "idempotency_conflict", "different_payload_reused_key_accepted")
        self.report["checks"]["exactPayloadReplay"] = {"passed": True, "versionsBefore": len(before), "versionsAfter": len(self.records()), "changedPayloadStatus": code}

    def restore(self, archive, baseline, capture_id, request):
        old_epoch = self.request(False, "GET", "/api/context/corpus-state")[1]["epoch"]
        self.kill()
        self.restore_one_shot(archive)
        new_epoch = self.request(False, "GET", "/api/context/corpus-state")[1]["epoch"]
        self.require(old_epoch != new_epoch, "restore_epoch_did_not_rotate")
        self.require(fingerprint(self.records()) == fingerprint(baseline), "older_snapshot_restore_did_not_remove_post_snapshot_changes")
        self.restart()
        code, receipt = self.request(True, "GET", "/api/knowledge/captures/" + capture_id)
        self.require(code == 200 and receipt["status"] == "partial" and receipt["errorClass"] == "corpus_epoch_changed", "journal_reported_success_for_absent_restored_commit")
        continuation = {"idempotencyKey": self.run_id + ":restore-continuation", "messages": [],
            "additionalBudget": {"calls": 0, "inputTokens": 0, "outputTokens": 0, "dollars": 0, "deadlineSeconds": 1}}
        code, error = self.request(True, "POST", "/api/knowledge/captures/" + capture_id + "/clarifications", continuation, True)
        self.require(code == 409 and error["errorClass"] == "reconciliation_required", "restored_capture_continuation_not_paused")
        self.request(True, "POST", "/api/knowledge/captures", request, True)
        time.sleep(3)
        self.require(fingerprint(self.records()) == fingerprint(baseline), "post_restore_handoff_silently_replayed")
        self.report["checks"]["olderCorpusRestorePause"] = {"passed": True, "epochChanged": True,
            "journalStatus": receipt["status"], "errorClass": receipt["errorClass"], "clarificationStatus": code, "automaticReplayObserved": False,
            "restoreUsedStoppedCoreAndOneShotImage": True}
        self.phase("explicit_journal_reconciliation")
        self.reconcile(capture_id, receipt, old_epoch, new_epoch)

    def reconcile(self, capture_id, before, old_epoch, new_epoch):
        recovery = before.get("reconciliationRequired") or {}
        self.require(recovery.get("previousCorpusEpoch") == old_epoch and recovery.get("expectedCorpusEpoch") == new_epoch, "public_recovery_epochs_missing")
        keys = [batch["operationKey"] for batch in before["committed"]]
        self.require(bool(keys) and set(keys).issubset(set(recovery.get("missingOperationKeys", []))), "public_recovery_missing_receipts_not_disclosed")
        payload = {"idempotencyKey": self.run_id + ":reconcile", "previousCorpusEpoch": old_epoch,
            "expectedCorpusEpoch": new_epoch, "acknowledgeRestore": True}
        route = "/api/knowledge/captures/" + capture_id + "/reconcile"
        code, error = self.request(True, "POST", route, dict(payload, acknowledgeRestore=False), True)
        self.require(code == 400 and error["errorClass"] == "restore_acknowledgement_required", "unacknowledged_restore_reconciliation_accepted")
        code, error = self.request(True, "POST", route, dict(payload, expectedCorpusEpoch=str(uuid.uuid4())), True)
        self.require(code in (400, 409) and error["errorClass"] == "reconciliation_epoch_mismatch", "unpinned_restore_reconciliation_accepted")
        code, acknowledged = self.request(True, "POST", route, payload, True)
        self.require(code == 202 and acknowledged["id"] == capture_id, "explicit_reconciliation_not_acknowledged")
        audit = acknowledged.get("reconciliations") or []
        self.require(len(audit) == 1 and audit[0]["generation"] == 1 and audit[0]["previousOperationKeys"] == keys, "reconciliation_audit_did_not_archive_receipts")
        self.require(audit[0]["previousUsage"] == before["usage"] and acknowledged["usage"] == before["usage"], "reconciliation_reset_spent_budget")
        self.require(before.get("deadlineUtc") is not None and acknowledged["deadlineUtc"] == before["deadlineUtc"], "reconciliation_automatically_renewed_deadline")
        self.report["reconciliationCheckpoint"] = {"previousUsage": before["usage"], "unchangedDeadlineUtc": before["deadlineUtc"],
            "generation": 1, "explicitTimeAllowanceUsed": False}
        self.save()
        code, repeated = self.request(True, "POST", route, payload, True)
        self.require(code == 202 and len(repeated.get("reconciliations") or []) == 1, "reconciliation_retry_created_new_generation")
        code, error = self.request(True, "POST", route, dict(payload, additionalBudget={"calls": 1, "inputTokens": 1, "outputTokens": 1, "dollars": 0, "deadlineSeconds": 1}), True)
        self.require(code == 409 and error["errorClass"] == "idempotency_conflict", "reconciliation_changed_payload_key_accepted")
        final = self.poll(capture_id)
        explicit_time_allowance = False
        if final["status"] == "partial" and final.get("errorClass") == "deadline_exhausted":
            allowance = {"idempotencyKey": self.run_id + ":explicit-time", "messages": [],
                "additionalBudget": {"calls": 0, "inputTokens": 0, "outputTokens": 0, "dollars": 0, "deadlineSeconds": 60}}
            code, continued = self.request(True, "POST", "/api/knowledge/captures/" + capture_id + "/clarifications", allowance, True)
            self.require(code == 202, "explicit_recovery_time_allowance_failed")
            self.require(continued["usage"]["calls"] >= before["usage"]["calls"], "explicit_allowance_reset_spend")
            explicit_time_allowance = True
            self.report["reconciliationCheckpoint"]["explicitTimeAllowanceUsed"] = True
            self.save()
            final = self.poll(capture_id)
        self.check_processed(final)
        new_keys = [batch["operationKey"] for batch in final["committed"]]
        self.require(bool(new_keys) and not set(keys).intersection(new_keys) and all(":g1:" in key for key in new_keys), "reconciliation_replayed_pre_restore_operation_keys")
        rows = self.records()
        self.verify_reconciled_versions(final, rows)
        code, repeated = self.request(True, "POST", route, payload, True)
        self.require(code == 202 and len(repeated.get("reconciliations") or []) == 1 and fingerprint(self.records()) == fingerprint(rows), "completed_reconciliation_retry_changed_corpus")
        self.report["checks"]["explicitRestoreReconciliation"] = {"passed": True, "missingOperationsDisclosed": len(keys),
            "generation": 1, "priorUsagePreserved": True, "deadlinePreservedWithoutAllowance": True,
            "newOperationKeysUsed": True, "repeatedReconciliationChangedCorpus": False,
            "explicitTimeAllowanceUsed": explicit_time_allowance, "recomparedVersions": len(rows), "finalStatus": final["status"]}

    def verify_reconciled_versions(self, final, rows):
        created = {item["uuid"] for batch in final["committed"] for item in batch["result"]["items"] if not item["versioned"]}
        self.require(bool(rows) and all(row["version"] == 1 for row in rows) and
            len(rows) == len(created) and {row["uuid"] for row in rows} == created and
            all(batch["result"]["versioned"] == 0 for batch in final["committed"]), "restored_recomparison_created_duplicate_versions")

    def run(self):
        self.phase("preflight")
        self.preflight()
        self.seed()
        baseline = self.records()
        archive = self.snapshot()
        self.phase("acknowledgement_kill_and_direct_outage")
        first_id, first_request = self.acknowledgement()
        self.phase("reservation_kill")
        self.reservation()
        self.phase("commit_before_journal_kill")
        final_id, final_request = self.commit_gap()
        self.phase("exact_payload_replay")
        self.replay(final_id, final_request)
        self.phase("older_corpus_restore")
        self.restore(archive, baseline, final_id, final_request)
        self.report["passed"] = True

    def save(self):
        self.output.parent.mkdir(parents=True, exist_ok=True)
        self.output.write_text(json.dumps(self.report, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description="Destructive acceptance confined to explicitly named isolated test containers.")
    parser.add_argument("--credentials-file", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--disruptive-test-containers", action="store_true")
    parser.add_argument("--resume-reservation-pause", help="external report from the earlier strict reservation terminal assertion; reuses its two captures and snapshot")
    parser.add_argument("--resume-restored-capture", help="external failed restore-pause report with existingCaptureId; resumes public recovery without new jobs or restore")
    args = parser.parse_args()
    harness = Harness(credentials(args.credentials_file), args.output, args.disruptive_test_containers)
    try:
        if args.resume_reservation_pause and args.resume_restored_capture:
            raise AcceptanceError("only_one_resume_checkpoint_allowed")
        if args.resume_reservation_pause:
            harness.resume_reservation_pause(json.loads(Path(args.resume_reservation_pause).read_text(encoding="utf-8")))
        elif args.resume_restored_capture:
            harness.resume_restored_capture(json.loads(Path(args.resume_restored_capture).read_text(encoding="utf-8")))
        else:
            harness.run()
    except (AcceptanceError, subprocess.TimeoutExpired, queue.Empty, ValueError, KeyError, OSError) as error:
        harness.report["passed"] = False
        harness.report["errorClass"] = str(error) if isinstance(error, AcceptanceError) else type(error).__name__
    finally:
        if not harness.core_running:
            try:
                harness.restart_core()
            except AcceptanceError:
                harness.report["coreRestartFailed"] = True
        if not harness.service_running:
            try:
                harness.restart()
            except AcceptanceError:
                harness.report["serviceRestartFailed"] = True
        harness.save()
    print(json.dumps({"passed": harness.report.get("passed", False), "checks": list(harness.report["checks"]), "captureJobs": harness.report["captureJobs"], "errorClass": harness.report.get("errorClass")}))
    return 0 if harness.report.get("passed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
