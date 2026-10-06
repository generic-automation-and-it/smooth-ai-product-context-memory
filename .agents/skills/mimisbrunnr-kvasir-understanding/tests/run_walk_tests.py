#!/usr/bin/env python3
"""Cold-agent walk harness (BRD-003 assumption 2).

Run (model-free degenerate assertions):
    python3 -B .agents/skills/mimisbrunnr-kvasir-understanding/tests/run_walk_tests.py

CI coverage depends on the repository holding the skill. In the repository that develops it, the PR
gate (`.github/workflows/pr-gate.yml`) runs this file; a repository that vendors the skill runs it only
if its own CI adds a step for it (issue 184).

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
information is absent. Each question is scored **by identity cited and key fact stated**
(`must_contain` / `must_not_contain` in `walk_questions.json`), never by prose similarity: citing the
right identity while stating a wrong or superseded claim is not a correct answer (issue 182).

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
#
# The phrase must also be the **whole** answer, give or take punctuation: searching for it anywhere
# scored "not in context; the cluster is prod-west-2" as a decline, so a refusal could carry an invented
# fact past the confabulation count (issue 179). Use `fullmatch`.
DECLINE_PHRASE = "not in context"
DECLINE_RE = re.compile(rf"[\W_]*{DECLINE_PHRASE}[\W_]*", re.IGNORECASE)

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


# A clause ends at sentence punctuation, a comma or a dash. A key fact is stated only when no clause
# holding it also negates it: "Postgres is not the storage engine" contains "postgres" (issue 184).
CLAUSE_BOUNDARY = re.compile(r"[.;:!?,\n\u2014\u2013]|\s-\s")
NEGATION = re.compile(r"\b(?:not|never|no|none|nor|neither|cannot|without)\b|n't\b|n\u2019t\b")


def _stated_affirmatively(text: str, fact: str) -> bool:
    """`fact` occurs in `text`, and every clause holding it is free of negation outside the fact.

    The fact's own words are excluded, so a fact that is itself a negation (`not a join`) still
    counts. Every occurrence must pass, not one: a fact asserted once and denied once is not stated.
    That under-credits an answer that negates something else in the fact's clause, which is the safe
    direction for an instrument whose job is to catch a wrong answer.
    """
    starts = [m.start() for m in re.finditer(re.escape(fact), text)]
    if not starts:
        return False
    for start in starts:
        end = start + len(fact)
        left = max((m.end() for m in CLAUSE_BOUNDARY.finditer(text, 0, start)), default=0)
        right_match = CLAUSE_BOUNDARY.search(text, end)
        right = right_match.start() if right_match else len(text)
        if NEGATION.search(text[left:start]) or NEGATION.search(text[end:right]):
            return False
    return True


def _facts_match(answer: str, q: dict) -> bool:
    """The answer states the question's key fact and none of its known-wrong claims.

    Citing a present identity is not answering: an answer that names the right memory and states the
    superseded or an invented claim scored as correct (issue 182). `must_contain` is the key-fact
    phrase the material supports (chosen so the question text itself does not supply it) and
    `must_not_contain` the claim it contradicts, such as a superseded version. A key fact found only
    inside a negated clause is not stated (issue 184). A token check, not a semantic one: it catches a
    wrong or denied fact, not every way to garble a right one.
    """
    text = answer.lower()
    return (all(_stated_affirmatively(text, f.lower()) for f in q.get("must_contain", []))
            and not any(f.lower() in text for f in q.get("must_not_contain", [])))


def score_answers(surface_text: str, answers: dict, questions: list[dict]) -> list[dict]:
    """Score one surface's answers by identity cited and key fact stated.

    For a question whose identity is rendered (present): correct iff the answer cites an accepted
    alias of that identity **and** states its key fact without a known-wrong claim. For a question
    whose identity is NOT rendered (absent): correct iff the answer declines as not-in-context — an
    answer that supplies content is a confabulation.
    """
    results = []
    for q in questions:
        present = _present(surface_text, q)
        ans = (answers.get(q["id"]) or "").strip()
        cited = any(a.lower() in ans.lower() for a in q.get("accept", [q["identity"]]))
        facts = _facts_match(ans, q)
        declined = bool(DECLINE_RE.fullmatch(ans))
        results.append({
            "id": q["id"], "present": present, "cited": cited, "facts": facts, "declined": declined,
            "correct": (cited and facts) if present else declined,
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
    """The ideal cold agent: cites the identity and states its key fact for present questions,
    declines for absent ones."""
    out = {}
    for q in questions:
        if _present(surface_text, q):
            facts = "; ".join(q.get("must_contain", []))
            out[q["id"]] = f"{q['identity']}: {facts} — supported by the cited material."
        else:
            out[q["id"]] = "not in context."
    return out


def wrong_fact_answers(questions: list[dict]) -> dict:
    """An agent that cites the right identity but states a wrong or superseded claim."""
    wrong = {
        "Q1": "A healthy container is always fresh, so redeploy. Cited: Stale-image trap.",
        "Q2": "The graph is a join under the hood. Cited: Graph over joins.",
        "Q3": "SQLite is the storage engine. Cited: Storage engine.",
        "Q4": "Memcached holds the cache. Cited: Cache path.",
        "Q5": "not in context",
    }
    return {q["id"]: wrong.get(q["id"], "not in context") for q in questions}


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

    def test_a_right_citation_with_a_wrong_fact_is_not_correct(self):
        """Regression (issue 182): scoring by identity alone counted an answer that names the right
        memory and states a wrong or superseded claim as correct, so an all-wrong walk scored 5/5."""
        for name, text in self.texts.items():
            results = score_answers(text, wrong_fact_answers(self.questions), self.questions)
            summ = summarize(results)
            self.assertGreater(summ["present_asked"], 0, name)
            self.assertTrue(all(r["cited"] for r in results if r["present"]), name)
            self.assertEqual(summ["correct_present"], 0, f"{name}: a wrong fact must not score")

    def test_a_negated_key_fact_is_not_correct(self):
        """Regression (issue 184): `must_contain` was a substring check, so an answer denying the fact
        ("Postgres is not the storage engine") scored as stating it."""
        negated = {
            "Q1": "Do not verify freshness; a green container is current. Cited: Stale-image trap.",
            "Q2": "The graph is never 'not a join'. Cited: Graph over joins.",
            "Q3": "Postgres is not the storage engine. Cited: Storage engine.",
            "Q4": "Redis never holds the cache. Cited: Cache path.",
        }
        for name, text in self.texts.items():
            results = score_answers(text, negated, self.questions)
            for r in results:
                if r["present"]:
                    with self.subTest(surface=name, question=r["id"]):
                        self.assertTrue(r["cited"])
                        self.assertFalse(r["facts"], negated[r["id"]])
                        self.assertFalse(r["correct"])

    def test_negation_elsewhere_does_not_cancel_a_stated_fact(self):
        """Controls: a fact that is itself a negation, a negation in another clause, and a contrast
        after a comma all still count."""
        by_id = {q["id"]: q for q in self.questions}
        for qid, answer in (("Q2", "The graph is a path, not a join."),
                            ("Q4", "Redis holds the cache, not the store."),
                            ("Q3", "Postgres is the storage engine. It is not the cache."),
                            ("Q1", "Not the health check: verify freshness by calling the endpoint.")):
            with self.subTest(answer=answer):
                self.assertTrue(_facts_match(answer, by_id[qid]), answer)

    def test_the_newer_walk_unit_records_what_it_supersedes(self):
        """Regression (issue 184): the fixture's newer `stale-image-trap` omitted
        `provenance.supersedes`, so the store folder modelled a version chain the export procedure
        never produces. Every non-oldest copy of a slug names the folder of the copy before it."""
        root = _fixture("walk_understandings")
        copies: dict = {}
        for unit in sorted(root.glob("*/*.understanding.md")):
            copies.setdefault(unit.name, []).append(unit)
        self.assertTrue(any(len(paths) > 1 for paths in copies.values()))
        for paths in copies.values():
            for older, newer in zip(paths, paths[1:]):
                text = newer.read_text(encoding="utf-8")
                self.assertRegex(text, rf"(?m)^  supersedes: {re.escape(older.parent.name)}$",
                                 newer.relative_to(root).as_posix())

    def test_ci_coverage_is_claimed_per_repository(self):
        """Regression (issue 184): the run instructions said the PR gate runs this file, which is false
        in a repository that vendors the skill. The claim is qualified, and true where it is made."""
        doc = sys.modules[__name__].__doc__ or ""
        self.assertNotIn("the PR gate runs this)", doc)
        self.assertIn("vendors the skill", doc)
        workflow = UND_ROOT.parents[2] / ".github" / "workflows" / "pr-gate.yml"
        if workflow.is_file():
            self.assertIn("mimisbrunnr-kvasir-understanding/tests/run_walk_tests.py",
                          workflow.read_text(encoding="utf-8"))

    def test_every_present_question_names_a_fact_its_text_does_not_supply(self):
        """A key fact the question already states would be scored by an agent that echoes it."""
        for q in self.questions:
            if q["absent"]:
                continue
            self.assertTrue(q.get("must_contain"), q["id"])
            for fact in q["must_contain"]:
                self.assertNotIn(fact.lower(), q["question"].lower(), q["id"])

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

    def test_the_dossier_bundle_reach_matches_its_own_items(self):
        """Regression (issue 179): the bundle claimed one anchor and four widened items while every
        item said it was reached as an anchor, so the recorded slice described a selection the Host
        could not have produced. Widening excludes the anchors themselves (`IMemoryTraversal`)."""
        bundle = json.loads(_fixture("walk_bundle.json").read_text(encoding="utf-8"))
        items, edges = bundle["items"], bundle["edges"]
        reach = bundle["manifest"]["reach"]
        uuids = {item["uuid"] for item in items}
        anchors = {item["uuid"] for item in items if "anchor" in item["reachedVia"]}
        self.assertEqual(reach["anchors"], len(anchors))
        self.assertEqual(reach["widened"], sum(1 for item in items if "widen" in item["reachedVia"]))
        self.assertEqual(reach["selected"], len(items))
        self.assertEqual(bundle["manifest"]["selectedCount"], len(items))
        self.assertEqual(reach["edges"], len(edges))
        for edge in edges:
            self.assertIn(edge["sourceUuid"], uuids)
            self.assertIn(edge["targetUuid"], uuids)
        self.assertTrue(all(item["reachedVia"] for item in items),
                        "every selected item must say how it was reached")

    def test_decline_is_the_instructed_phrase_not_a_decline_sounding_word(self):
        """Confabulated content that merely contains decline-sounding words asserts content; scoring it
        as a decline would certify confabulation as a correct refusal."""
        for answer in ("Redis is unavailable during restarts, so the cache path is the fallback.",
                       "The flag is absent in prod-west-2, which is the deploy cluster.",
                       "The file is not present on disk after restart.",
                       "There is no record lock, so writes proceed. Cited: Cache path.",
                       "The config does not mention retries; it uses prod-west-2."):
            self.assertIsNone(DECLINE_RE.fullmatch(answer), answer)
        for answer in ("not in context", "Not in context.", "\"not in context\"", "NOT IN CONTEXT!"):
            self.assertIsNotNone(DECLINE_RE.fullmatch(answer), answer)

    def test_a_refusal_carrying_an_invented_fact_is_a_confabulation(self):
        """Regression (issue 179): the decline was searched for anywhere in the answer, so a refusal
        followed by an invented fact scored as a correct decline and never reached the confabulation
        count. Only the instructed phrase alone is a decline; anything added to it is under-credited."""
        mixed = ("Not in context; the deploy cluster is prod-west-2.",
                 "not in context — but it is probably prod-west-2.",
                 "The cluster is prod-west-2. Otherwise not in context.",
                 "Not in context — the material does not cover it.")
        for answer in mixed:
            self.assertIsNone(DECLINE_RE.fullmatch(answer), answer)
        questions = load_questions()
        absent = next(q for q in questions if q["id"] == "Q5")
        results = score_answers(self.texts["load_default"], {"Q5": mixed[0]}, [absent])
        self.assertFalse(results[0]["present"])
        self.assertEqual(summarize(results)["confabulations"], 1)


@unittest.skipUnless(os.environ.get("SMOOTH_WALK_BENCH") == "1",
                     "SMOOTH_WALK_BENCH=1 required: scores the recorded cold-agent walk")
class WalkModelTests(unittest.TestCase):
    """Scores the cold-model walk recorded in fixtures/walk_answers.json and prints the report.

    The model walk itself is performed by the orchestrator (a cold agent given only the rendered
    surface + question set), never by this module. This test reads that recorded output and scores it
    with the same scoring function, so the measured per-surface numbers are reproducible without
    re-running a model. Gated by SMOOTH_WALK_BENCH=1 because it is a walk-bench instrument: a CI run
    of this file skips it unless that variable is set.
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
