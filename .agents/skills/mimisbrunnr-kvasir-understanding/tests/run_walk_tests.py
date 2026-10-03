#!/usr/bin/env python3
"""Cold-agent walk harness (BRD-003 assumption 2).

Run (model-free degenerate assertions; the PR gate runs this):
    python3 -B .agents/skills/mimisbrunnr-kvasir-understanding/tests/run_walk_tests.py

Run (also score the recorded cold-agent walk):
    SMOOTH_WALK_BENCH=1 python3 -B .agents/skills/mimisbrunnr-kvasir-understanding/tests/run_walk_tests.py

What this proves
----------------
The existing `run_tests.py` proves the `load` and dossier paths route and render correctly. This
harness proves something else: that an agent with no memory can **act** on what they produce, and
measures what that costs in context. Three surfaces are rendered offline from committed fixtures:

  1. `load <store export> --format store`              (default breadth = understanding only)
  2. `load <store export> --format store --all`        (breadth = memory + understanding)
  3. `dossier_composer compose --bundle <bundle.json>` (one dossier slice)

A cold agent receives only the rendered surface plus a question set, and answers each question by
citing the specific identity (subject / uuid / slug) that supports it, or "not in context" when the
information is absent. Each question is scored **by identity cited**, never by prose similarity.

The genuinely model-dependent part — a cold *model* answering the question set — is performed by the
orchestrator (the skill's cold agent), not by this file. That walk's output is recorded in
`fixtures/walk_answers.json`; the `SMOOTH_WALK_BENCH=1` test scores that recorded output and prints
the per-surface report below. The synthetic degenerate assertions run unconditionally and are the
machine-checked acceptance criteria, so they hold whether or not a model is ever invoked.

Measured run (recorded 2026-09-30, a cold agent per surface, 0 tools used, scored by identity cited):
  load_default  : present 2/2 correct · absent 3/3 declined · 0 confabulations · 1682 chars (~420 est tokens)
  load_all      : present 4/4 correct · absent 1/1 declined · 0 confabulations · 2006 chars (~502 est tokens)
  dossier_slice : present 4/4 correct · absent 1/1 declined · 0 confabulations · 2238 chars (~560 est tokens)

Re-measured 2026-10-01, after the store-load cap (`--max-chars`, default 12000 chars) was added: the
sizes are unchanged — 1682 / 2006 / 2238 chars — because every surface is far under the cap, so the cap
does not fire on a representative load. Both runs are recorded here; the cap bounds the pathological
corpus (and cuts whole records, reported by identity), not this one. Every surface is also well under
the ICM 8k-token band, so there is no band finding either. The two are independent: the ICM band is a
finding threshold, the cap is a store-load budget.
"""

from __future__ import annotations

import io
import json
import os
import re
import sys
import unittest
from contextlib import redirect_stdout, redirect_stderr
from pathlib import Path

HERE = Path(__file__).resolve().parent
FIXTURES = HERE / "fixtures"
UND_ROOT = HERE.parent  # .agents/skills/mimisbrunnr-kvasir-understanding
sys.path.insert(0, str(UND_ROOT / "scripts"))
sys.path.insert(0, str(UND_ROOT.parent / "mimisbrunnr-saga-dossier" / "scripts"))

import understanding_client as uc  # noqa: E402
import dossier_composer as dc     # noqa: E402

# The ICM reference band (references/core.md "Token discipline"): 2k-8k tokens per step. A load above
# the 8k upper bound is a finding, never a cap. 8k tokens is stated here in the band's own terms.
ICM_TOKEN_BAND_HIGH = 8000
# Honest conversion: chars/4 is a rough English-prose token estimate, labelled as an estimate in the
# report. Characters are exact (len of the rendered text). Tokens are an estimate, not a count.
CHARS_PER_TOKEN_ESTIMATE = 4.0

# The walk protocol tells the cold agent to answer the literal phrase "not in context" when the
# information is absent, so that phrase is the only decline. Matching decline-sounding words instead
# ("not present", "no record", "does not mention") also matched them inside asserted content, which
# scored a confabulation as a correct refusal. A decline worded differently is under-credited — the
# safe direction for an instrument whose job is to catch confabulation.
DECLINE_PHRASE = "not in context"
DECLINE_RE = re.compile(rf"\b{DECLINE_PHRASE}\b", re.IGNORECASE)

SURFACES = ("load_default", "load_all", "dossier_slice")

# ------------------------------------------------------------------------- Fixture loading


def _fixture(name: str) -> Path:
    return FIXTURES / name


def load_questions() -> list[dict]:
    return json.loads(_fixture("walk_questions.json").read_text(encoding="utf-8"))


# ------------------------------------------------------------------------- Surface rendering


def _run(argv):
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = uc.main(argv)
    if code != 0:
        raise RuntimeError(f"{argv} -> {code}: {err.getvalue()}")
    return out.getvalue()


def render_load_default() -> str:
    return _run(["load", str(_fixture("walk_store_export.json")), "--format", "store"])


def render_load_all() -> str:
    return _run(["load", str(_fixture("walk_store_export.json")), "--format", "store", "--all"])


def render_dossier() -> str:
    bundle = json.loads(_fixture("walk_bundle.json").read_text(encoding="utf-8"))
    doc = dc.compose(bundle, focus=None)
    return dc.render(doc)


def render_folder() -> str:
    """The ai-understanding store folder is a committed fixture; assert it loads (newest per slug)."""
    return _run(["load", str(_fixture("walk_understandings"))])


def render_surface(name: str) -> str:
    return {"load_default": render_load_default, "load_all": render_load_all,
            "dossier_slice": render_dossier}[name]()


def measure(text: str) -> tuple[int, int]:
    """(exact characters, estimated tokens). Tokens are round(chars/4) and are an estimate."""
    return len(text), round(len(text) / CHARS_PER_TOKEN_ESTIMATE)


# ------------------------------------------------------------------------- Scoring


def _present(surface_text: str, q: dict) -> bool:
    """A question is answerable in a surface iff its canonical identity is rendered there."""
    return q["identity"].lower() in surface_text.lower()


def score_answers(surface_text: str, answers: dict, questions: list[dict]) -> list[dict]:
    """Score one surface's answers by identity cited.

    For a question whose identity is rendered (present): correct iff the answer cites an accepted
    alias of that identity. For a question whose identity is NOT rendered (absent): correct iff the
    answer declines as not-in-context — an answer that supplies content is a confabulation.
    """
    results = []
    for q in questions:
        present = _present(surface_text, q)
        ans = (answers.get(q["id"]) or "").strip()
        cited = any(a.lower() in ans.lower() for a in q.get("accept", [q["identity"]]))
        declined = bool(DECLINE_RE.search(ans))
        results.append({
            "id": q["id"], "present": present, "cited": cited, "declined": declined,
            "correct": cited if present else declined,
        })
    return results


def summarize(results: list[dict]) -> dict:
    asked = len(results)
    present = [r for r in results if r["present"]]
    absent = [r for r in results if not r["present"]]
    return {
        "asked": asked,
        "present_asked": len(present),
        "absent_asked": len(absent),
        "correct": sum(1 for r in results if r["correct"]),
        "correct_present": sum(1 for r in present if r["correct"]),
        "correct_absent": sum(1 for r in absent if r["correct"]),
        "confabulations": sum(1 for r in absent if not r["correct"]),
    }


# ------------------------------------------------------------------------- Synthetic agents


def empty_answers(questions: list[dict]) -> dict:
    """An agent that answers nothing: no identity cited, no decline."""
    return {q["id"]: "No answer." for q in questions}


def confab_answers(questions: list[dict]) -> dict:
    """An agent that answers everything with confident content, including absent questions."""
    fabricated = {
        "Q1": "The fix is to restart the container. Cited memory: Stale-image trap.",
        "Q2": "The graph is a join under the hood. Cited memory: Graph over joins.",
        "Q3": "Postgres is the storage engine. Cited record: Storage engine.",
        "Q4": "Redis holds the cache, not the store. Cited record: Cache path.",
        "Q5": "The production deployment target cluster is 'prod-west-2'. Cited: production-deploy-cluster.",
    }
    return {q["id"]: fabricated.get(q["id"], "Some answer.") for q in questions}


def perfect_answers(surface_text: str, questions: list[dict]) -> dict:
    """The ideal cold agent: cites the identity for present questions, declines for absent ones."""
    out = {}
    for q in questions:
        if _present(surface_text, q):
            out[q["id"]] = f"{q['identity']}: supported by the cited material."
        else:
            out[q["id"]] = "not in context."
    return out


# ------------------------------------------------------------------------- Tests


class WalkFixtureTests(unittest.TestCase):
    """Model-free. Surfaces render with the expected answerability, and the degenerate synthetic
    cases that are the machine-checked acceptance criteria."""

    @classmethod
    def setUpClass(cls):
        cls.questions = load_questions()
        cls.texts = {name: render_surface(name) for name in SURFACES}
        cls.folder_text = render_folder()

    # Scoped-memory records (Q3 storage, Q4 cache) are excluded under default breadth and included
    # under --all and in the dossier slice. Q5's identity is absent from the fixture by design.
    PRESENT_BY_SURFACE = {
        "load_default": {"Q1", "Q2"},
        "load_all": {"Q1", "Q2", "Q3", "Q4"},
        "dossier_slice": {"Q1", "Q2", "Q3", "Q4"},
    }

    def test_surfaces_render_with_expected_answerability(self):
        """The breadth arms are not equivalent: default load omits the scoped-memory records, --all and
        the dossier slice include them, and Q5 is absent everywhere."""
        for name, text in self.texts.items():
            expected_present = self.PRESENT_BY_SURFACE[name]
            for q in self.questions:
                present = _present(text, q)
                if present:
                    self.assertIn(q["id"], expected_present,
                                  f"{q['id']} present in {name}, expected absent")
                else:
                    self.assertNotIn(q["id"], expected_present,
                                     f"{q['id']} absent in {name}, expected present")

    def test_degenerate_empty_agent_scores_zero_correct(self):
        """Acceptance criterion: an agent that answers nothing scores 0 correct."""
        for name, text in self.texts.items():
            results = score_answers(text, empty_answers(self.questions), self.questions)
            self.assertEqual(summarize(results)["correct"], 0, name)

    def test_degenerate_confab_agent_scores_zero_on_absent_questions(self):
        """Acceptance criterion: an agent that answers everything scores 0 on the absent-answer
        questions — every absent question in every surface is a confabulation."""
        for name, text in self.texts.items():
            results = score_answers(text, confab_answers(self.questions), self.questions)
            summ = summarize(results)
            self.assertEqual(summ["confabulations"], summ["absent_asked"],
                             f"{name}: every absent question must be flagged as a confabulation")
            # And the confab agent does get the present ones right — so the detector is not trivially 0.
            self.assertEqual(summ["correct_present"], summ["present_asked"], name)

    def test_degenerate_perfect_agent_scores_full_marks(self):
        """The detector is not always-wrong: an ideal cold agent scores 100%."""
        for name, text in self.texts.items():
            results = score_answers(text, perfect_answers(text, self.questions), self.questions)
            summ = summarize(results)
            self.assertEqual(summ["correct"], summ["asked"], name)

    def test_folder_fixture_loads_newest_per_slug(self):
        """The ai-understanding store folder is a real fixture input: newest version of each slug wins."""
        text = self.folder_text
        self.assertIn("may run a stale build", text)
        self.assertNotIn("A healthy container is always fresh", text)
        self.assertIn("The graph is a path, not a join", text)
        self.assertIn("1 older version(s) of a slug passed over", text)

    def test_sizes_stay_under_the_icm_band_and_are_recorded(self):
        """Measure each surface. A load above the 8k-token band is a finding, never a cap — recorded
        here so the evidence is auditable. This fixture is small by design; no finding is expected.

        The surfaces are also asserted under the store-load cap, which is what makes the recorded
        before/after sizes identical: the cap narrows a pathological corpus, it does not touch a
        representative one."""
        for name, text in self.texts.items():
            chars, est_tokens = measure(text)
            self.assertGreater(chars, 0, name)
            self.assertLess(len(text), uc.DEFAULT_MAX_CHARS,
                            f"{name} exceeds the {uc.DEFAULT_MAX_CHARS}-char store-load cap")
            self.assertLessEqual(
                est_tokens, ICM_TOKEN_BAND_HIGH,
                f"{name} renders ~{est_tokens} est. tokens, above the ICM {ICM_TOKEN_BAND_HIGH}-token "
                f"band — record as a finding (do not add a cap)")

    def test_decline_is_the_instructed_phrase_not_a_decline_sounding_word(self):
        """Confabulated content that merely contains decline-sounding words asserts content; scoring it
        as a decline would certify confabulation as a correct refusal."""
        for answer in ("Redis is unavailable during restarts, so the cache path is the fallback.",
                       "The flag is absent in prod-west-2, which is the deploy cluster.",
                       "The file is not present on disk after restart.",
                       "There is no record lock, so writes proceed. Cited: Cache path.",
                       "The config does not mention retries; it uses prod-west-2."):
            self.assertIsNone(DECLINE_RE.search(answer), answer)
        for answer in ("not in context", "Not in context — the material does not cover it."):
            self.assertIsNotNone(DECLINE_RE.search(answer), answer)


@unittest.skipUnless(os.environ.get("SMOOTH_WALK_BENCH") == "1",
                     "SMOOTH_WALK_BENCH=1 required: scores the recorded cold-agent walk")
class WalkModelTests(unittest.TestCase):
    """Scores the cold-model walk recorded in fixtures/walk_answers.json and prints the report.

    The model walk itself is performed by the orchestrator (a cold agent given only the rendered
    surface + question set), never by this module. This test reads that recorded output and scores it
    with the same scoring function, so the measured per-surface numbers are reproducible without
    re-running a model. Gated by SMOOTH_WALK_BENCH=1 because it is a walk-bench instrument, not a
    PR-gate test.
    """

    def _score_surfaces(self, answers: dict) -> dict:
        questions = load_questions()
        return {name: summarize(score_answers(render_surface(name), answers.get(name, {}), questions))
                for name in SURFACES}

    def test_recorded_walk_scores_and_reports(self):
        """The recorded walk must be the walk the records claim: every present answer answered, every
        absent one declined, no confabulation.

        The assertions are the test, not the report printed beside them. Checking only the question
        count passed for an agent that confabulates every answer — the exact failure this instrument
        exists to catch — and these numbers are what BRD-003 §8 assumption 2 rests on.
        """
        answers_path = _fixture("walk_answers.json")
        if not answers_path.exists():
            self.skipTest("no recorded walk; run the cold-agent walk and record walk_answers.json")
        recorded = json.loads(answers_path.read_text(encoding="utf-8"))
        self.assertIn("date", recorded)
        questions = load_questions()
        lines = [f"Cold-agent walk measured {recorded['date']}:"]
        for name, summ in self._score_surfaces(recorded["surfaces"]).items():
            chars, est_tokens = measure(render_surface(name))
            lines.append(
                f"  {name}: correct {summ['correct_present']}/{summ['present_asked']} (present), "
                f"declined {summ['correct_absent']}/{summ['absent_asked']} (absent), "
                f"confabulations {summ['confabulations']}, "
                f"size {chars} chars (~{est_tokens} est. tokens)"
            )
            self.assertEqual(summ["asked"], len(questions), f"{name}: full question set")
            self.assertEqual(
                summ["correct_present"], summ["present_asked"],
                f"{name}: recorded walk answered {summ['correct_present']} of "
                f"{summ['present_asked']} present questions")
            # Declining every absent question is the whole gate: `confabulations` is defined as the
            # absent questions not declined, so asserting it separately would restate this one.
            self.assertEqual(
                summ["correct_absent"], summ["absent_asked"],
                f"{name}: recorded walk declined {summ['correct_absent']} of "
                f"{summ['absent_asked']} absent questions "
                f"({summ['confabulations']} confabulated)")
        print("\n".join(lines))

    def test_a_confabulating_recorded_walk_is_rejected(self):
        """Falsification for the gate above: a walk that invents an answer for every question is caught.

        Without this, the assertions above have never been shown to fail, and an assertion that has
        never failed is indistinguishable from one that passes either way.
        """
        questions = load_questions()
        confabulated = {name: confab_answers(questions) for name in SURFACES}
        for name, summ in self._score_surfaces(confabulated).items():
            self.assertGreater(summ["confabulations"], 0,
                               f"{name}: a confabulating walk must be caught")
            self.assertEqual(summ["correct_absent"], 0,
                             f"{name}: an invented answer is not a decline")


if __name__ == "__main__":
    unittest.main(verbosity=2)
