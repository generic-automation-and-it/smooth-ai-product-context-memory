import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).with_name("run_evaluation.py")
spec = importlib.util.spec_from_file_location("evaluation",SCRIPT)
evaluation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evaluation)


class EvaluationTests(unittest.TestCase):
    def test_fixture_freeze_and_distinct_holdout(self):
        data = evaluation.fixture()
        development = {case["question"] for case in data["retrieval"] if case["split"] == "development"}
        heldout = {case["question"] for case in data["retrieval"] if case["split"] == "heldout"}
        self.assertFalse(development & heldout)
        self.assertGreater(len(heldout),len(development))
        with patch.object(Path,"read_bytes",return_value=b'{"version":"changed"}'):
            with self.assertRaisesRegex(evaluation.client_module.ClientError,"frozen_fixture_hash_changed"):
                evaluation.fixture()

    def test_confident_empty_result_cannot_pass_quality(self):
        case = evaluation.fixture()["retrieval"][0]
        record_ids = {record["id"]:record["id"] for record in evaluation.fixture()["corpus"]}
        score = evaluation.score_response(case,{"brief":"I am completely certain.","coverage":[{"outcome":"supported"}],"confidence":1},record_ids)
        self.assertEqual(score["requiredDetailPasses"],0)
        self.assertEqual(score["citedExpectedEvidence"],0)
        self.assertTrue(score["manualReviewRequired"])

    def test_wrong_evidence_and_forbidden_claim_are_visible(self):
        case = evaluation.fixture()["retrieval"][0]
        ids = {record["id"]:record["id"] for record in evaluation.fixture()["corpus"]}
        score = evaluation.score_response(case,{"brief":"all refunds under $20 are automatically approved refund-current", "evidence":[],"coverage":[{"outcome":"supported"}]},ids)
        self.assertEqual(score["unsupportedCitationIds"],["refund-current"])
        self.assertEqual(len(score["forbiddenStatementMatches"]),1)

    def test_duplicate_and_unshipped_lifecycle_gate(self):
        duplicate = evaluation.fixture()["capture"][0]
        original = [{"uuid":"one","statement":"checkout","sources":[]}]
        receipt = {"id":"capture-1","status":"processed"}
        score = evaluation.score_capture(duplicate,original,original+[dict(original[0],uuid="two")],receipt,receipt)
        self.assertFalse(score["lifecycleOrDuplicatePass"])
        intent = evaluation.fixture()["capture"][2]
        shipped = [{"uuid":"new","statement":"Pine 9", "sources":[{"evidence":{"category":"observed_implementation"}}]}]
        score = evaluation.score_capture(intent,[],shipped,receipt,receipt)
        self.assertFalse(score["lifecycleOrDuplicatePass"])

    def test_context_estimate_counts_full_returned_evidence(self):
        short = evaluation.estimate_tokens({"brief":"answer"})
        full = evaluation.estimate_tokens({"brief":"answer","evidence":[{"body":"x"*10000}]})
        self.assertGreater(full,short+2000)

    def test_replay_gate_detects_version_or_source_growth(self):
        case = evaluation.fixture()["capture"][0]
        receipt = {"id":"capture-1","status":"processed"}
        records = [{"uuid":"one","version":1,"statement":"checkout","sources":[]}]
        self.assertTrue(evaluation.score_capture(case,records,records,receipt,receipt,records)["replayCorpusUnchangedPass"])
        changed = [dict(records[0],version=2)]
        self.assertFalse(evaluation.score_capture(case,records,records,receipt,receipt,changed)["replayCorpusUnchangedPass"])

    def test_capture_observation_does_not_use_frozen_midnight(self):
        class Core:
            def request(self, method, path, payload):
                self.payload = payload
                return {"items":[]}
        core = Core()
        evaluation.query_capture_corpus(core,{"repo":"synthetic","groupUuid":"one"})
        self.assertNotIn("asOf",core.payload)

    def test_failed_noop_does_not_pass_duplicate_gate(self):
        case = evaluation.fixture()["capture"][0]
        records = [{"uuid":"one","version":1,"sources":[]}]
        receipt = {"id":"capture-1","status":"partial","committed":[]}
        self.assertFalse(evaluation.score_capture(case,records,records,receipt,receipt,records)["lifecycleOrDuplicatePass"])


if __name__ == "__main__":
    unittest.main()
