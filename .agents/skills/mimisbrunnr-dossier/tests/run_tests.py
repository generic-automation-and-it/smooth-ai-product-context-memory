#!/usr/bin/env python3
"""Committed L0 harness for mimisbrunnr-dossier (HLD-005 NFR-01..07).

Run: python3 -B .agents/skills/mimisbrunnr-dossier/tests/run_tests.py

Covers the guarantees the dossier skill must satisfy from the HLD-005 Verification lists:
  NFR-04 completeness — reconciliation closes exactly; bounded omission reasons & finding taxonomy
      (unknown fails); every finding carries basis + scope; no store-wide phrasing;
      kind = understanding accounted; unreadable body omitted; provenance cycle reported + sort
      terminates; per-focus accompanies the same selected memories + carries every unfocused finding.
  NFR-05 attribution — zero uncited substantive statements; three captures -> all three citations;
      composition-authored text marked analysis with a basis; missing provenance visible; an imperative
      product rule stays attributed not directive; superseded/stale marked at point of use.
  NFR-07 fidelity — role-restricted rule + customer-specific exception survive; copies consolidate with
      all origins and are not presented as corroboration; differing-customer-scope claims not
      consolidated; budget-exceeded produces a reported omission, never a stripped claim.
  NFR-06 read-only — the skill exposes no write operation (capability-absence).

stdlib unittest; no external runner.
"""

from __future__ import annotations

import copy
import json
import os
import re
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))

import dossier_composer as dc  # noqa: E402

HERE = Path(__file__).resolve().parent
FIXTURES = HERE / "fixtures"


def _load(name):
    return json.loads((FIXTURES / name).read_text(encoding="utf-8"))


def _mk(uuid, name, stmt, kind="requirement", status="current", scope=("product", None),
        summ=None, source_ref="r", created="2026-01-01T10:00:00Z", body_state="inlined",
        valid_until=None, valid_from="2026-01-01", confidence=8):
    return {
        "uuid": uuid, "groupUuid": "g1", "name": name, "description": "",
        "statement": stmt, "contentSummary": summ if summ is not None else stmt,
        "kind": kind, "status": status, "confidence": confidence,
        "scopeDimension": scope[0], "scopeIdentifier": scope[1],
        "validFrom": valid_from, "validUntil": valid_until, "version": 1,
        "isCurrent": status == "current", "createdOn": created,
        "sources": [{"kind": "session", "reference": source_ref, "capturedAt": created}],
        "bodyText": stmt, "bodyState": body_state, "reachedVia": ["anchor"],
    }


def _bundle(items, edges=None, omitted=None, selected_count=None):
    edges = edges or []
    omitted = omitted or []
    count = selected_count if selected_count is not None else len(items) + len(omitted)
    return {
        "items": items, "edges": edges, "omitted": omitted,
        "manifest": {"selection": {}, "selectedCount": count, "reach": {}, "limitsHit": [],
                     "noMatch": False},
    }


# ---------------------------------------------------------------------------- NFR-04 completeness


class Nfr04ReconciliationTests(unittest.TestCase):
    def test_fixture_bundle_reconciles_exactly(self):
        """The load-bearing test: parse the produced dossier and assert the arithmetic closes
        exactly against the manifest count."""
        bundle = _load("reconciliation_bundle.json")
        judg = {"equivalences": [{"uuids": [str(u) for u in (
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            "33333333-3333-3333-3333-333333333333")], "meaning": "same default rule"}]}
        doc = dc.compose(bundle, focus=None, judgements=judg)
        rec = doc.reconciliation
        self.assertTrue(rec["closed"])
        self.assertEqual(rec["selected"], bundle["manifest"]["selectedCount"])
        self.assertEqual(rec["present"] + rec["consolidated"] + rec["omitted"], rec["selected"])
        # The reconciliation is stated in the dossier, not only asserted in the test (NFR-04).
        rendered = dc.render(doc)
        self.assertRegex(rendered, r"Reconciled: \d+ \+ \d+ \+ \d+ == \d+ ✓")

    def test_kind_understanding_accounts_in_the_same_arithmetic(self):
        """LADR-15: a kind=understanding item composes like any other kind — same closed arithmetic,
        cited to the same bar, no exemption."""
        items = [
            _mk("aaaaaaaa-0000-4000-8000-000000000001", "Und", "A path is not a join.",
                kind="understanding"),
            _mk("bbbbbbbb-0000-4000-8000-000000000002", "Memory", "Postgres is the store.",
                kind="architecture"),
        ]
        doc = dc.compose(_bundle(items), focus=None)
        self.assertTrue(doc.reconciliation["closed"])
        self.assertEqual(doc.reconciliation["selected"], 2)
        self.assertEqual(doc.lifecycle[items[0]["uuid"]], "current")
        # The understanding kinds are not exempt from the finding taxonomy.
        cats = {f["category"] for f in doc.findings}
        # They are examined like any other item (no-links-in-slice applies to both).
        self.assertIn("no-links-in-slice", cats)

    def test_omission_and_finding_categories_are_bounded_unknown_fails(self):
        """NFR-04: every omission reason and finding category is from the bounded set; an unknown
        value fails rather than being accepted silently."""
        # Unknown omission reason is rejected by validation.
        bad = {"items": [], "edges": [], "omitted": [{"uuid": "aaaaaaaa-0000-4000-8000-000000000001",
                                                      "reason": "because reasons"}],
               "manifest": {"selection": {}, "selectedCount": 1, "reach": {}, "limitsHit": [],
                            "noMatch": False}}
        with self.assertRaises(ValueError):
            dc.compose(bad, focus=None)
        # The omission reasons used by the deterministic side + the lens are the bounded set.
        self.assertEqual(set(dc.OMISSION_REASONS),
                         {"cap reached", "depth reached", "unreadable body",
                          "collapsed into another claim", "hidden by scope", "outside-focus"})
        self.assertEqual(set(dc.FINDING_CATEGORIES),
                         {"gap", "contradiction", "equivalence-uncertain", "superseded-still-referenced",
                          "stale", "unattributed", "no-links-in-slice", "weak-summary",
                          "provenance-cycle", "near-miss-tag"})
        # An unknown finding category supplied via judgement fails.
        items = [_mk("aaaaaaaa-0000-4000-8000-000000000001", "A", "The default is A.")]
        with self.assertRaises(ValueError):
            dc.compose(_bundle(items), focus=None,
                       judgements={"findings": [{"category": "mystery", "basis": "x"}]})

    def test_every_finding_carries_basis_scope_and_memory_identity(self):
        """NFR-04: every finding names memories by identity + version and carries basis + scope."""
        items = [
            _mk("aaaaaaaa-0000-4000-8000-000000000001", "A", "The default is A.",
                summ="short" if False else "A", created="2026-01-01T10:00:00Z"),
            _mk("bbbbbbbb-0000-4000-8000-000000000002", "B", "The default is B.",
                summ="B", created="2026-01-02T10:00:00Z"),
        ]
        doc = dc.compose(_bundle(items), focus=None,
                         judgements={"findings": [
                             {"category": "contradiction", "classification": "analysis",
                              "basis": "Two current claims are incompatible for the same circumstances.",
                              "memories": [{"uuid": items[0]["uuid"], "version": 1},
                                           {"uuid": items[1]["uuid"], "version": 1}]}]})
        self.assertTrue(doc.findings)
        for f in doc.findings:
            self.assertTrue(f.get("basis"), f)
            self.assertTrue(f.get("scope"), f)
            self.assertIn(f.get("classification"), ("observation", "analysis"))
            for m in f.get("memories", []):
                self.assertTrue(m.get("uuid"))
                self.assertGreaterEqual(m.get("version"), 1)

    def test_no_finding_phrases_a_slice_observation_as_store_wide(self):
        """LADR-13: no slice observation is stated as a store-wide or product-wide fact, and
        ``None detected`` is qualified by the examined scope."""
        # Two linked items, so there is no no-links-in-slice finding and "None detected" is emitted.
        items = [
            _mk("aaaaaaaa-0000-4000-8000-000000000001", "A", "The default is A."),
            _mk("bbbbbbbb-0000-4000-8000-000000000002", "B", "The default is B."),
        ]
        edges = [{"sourceUuid": items[1]["uuid"], "targetUuid": items[0]["uuid"],
                  "relation": "relates_to", "reason": "related"}]
        doc = dc.compose(_bundle(items, edges), focus=None)
        self.assertEqual(doc.findings, [])
        rendered = dc.render(doc)
        self.assertIn("None detected in the selected material in this bundle (2 memory(ies)).",
                      rendered)
        for f in doc.findings:
            self.assertNotRegex(f["scope"], r"\b(?:store|product|everywhere|all memories)\b")
            self.assertNotRegex(f["basis"], r"\b(?:no memory in the store|the store has no)\b")

    def test_unreadable_body_is_omitted_with_the_reason(self):
        """NFR-04: an unreadable body is an omission with the ``unreadable body`` reason, never a
        silent skip."""
        bundle_ = _bundle([], omitted=[{"uuid": "aaaaaaaa-0000-4000-8000-000000000001",
                                        "reason": "unreadable body"}])
        doc = dc.compose(bundle_, focus=None)
        self.assertTrue(doc.reconciliation["closed"])
        reasons = [o["reason"] for o in doc.omitted]
        self.assertEqual(reasons, ["unreadable body"])
        # It is listed in the dossier's omitted section, not dropped in silence.
        rendered = dc.render(doc)
        self.assertIn("unreadable body", rendered)

    def test_provenance_cycle_reported_and_sort_terminates(self):
        """LADR-07: a provenance cycle is reported as a finding and the sort still terminates."""
        items = [
            _mk("aaaaaaaa-0000-4000-8000-000000000001", "A", "Claim A.", created="2026-01-01T10:00:00Z"),
            _mk("bbbbbbbb-0000-4000-8000-000000000002", "B", "Claim B.", created="2026-01-02T10:00:00Z"),
        ]
        edges = [
            {"sourceUuid": items[1]["uuid"], "targetUuid": items[0]["uuid"], "relation": "supersedes", "reason": "x"},
            {"sourceUuid": items[0]["uuid"], "targetUuid": items[1]["uuid"], "relation": "supersedes", "reason": "y"},
        ]
        doc = dc.compose(_bundle(items, edges), focus=None)
        self.assertTrue(doc.reconciliation["closed"])
        # Sort terminates: every item is present (surfaced).
        self.assertEqual(sum(1 for c in doc.claims if c.get("surfaced")), 2)
        cats = [f["category"] for f in doc.findings]
        self.assertIn("provenance-cycle", cats)

    def test_claim_order_is_topological_not_business_key(self):
        """LADR-07: the dossier's claim order follows the deterministic topological order over the
        ordering relations, not business-key recency. A superseding claim that happens to be
        business-newer than the claim it replaces must not be reordered ahead of it; the superseded
        claim is read first."""
        # A supersedes B. A has the EARLIER business key (validFrom/createdOn), so business-key sort
        # would put A first; topological order (superseded first) must put B first.
        a = _mk("aaaaaaaa-0000-4000-8000-000000000001", "A", "The default is A.",
                created="2026-01-01T10:00:00Z")
        b = _mk("bbbbbbbb-0000-4000-8000-000000000002", "B", "The default is B.",
                created="2026-02-01T10:00:00Z")
        edges = [{"sourceUuid": a["uuid"], "targetUuid": b["uuid"], "relation": "supersedes", "reason": "x"}]
        doc = dc.compose(_bundle([a, b], edges), focus=None)
        self.assertTrue(doc.reconciliation["closed"])
        order = [c["origins"][0]["name"] for c in doc.claims if c.get("surfaced")]
        self.assertEqual(order, ["B", "A"])
        # The renderer emits them in the same order.
        rendered = dc.render(doc)
        self.assertLess(rendered.index("### B"), rendered.index("### A"))

    def test_consolidation_gates_on_derived_lifecycle(self):
        """NFR-07 / reviewer finding 1: the equivalence gate compares the derived lifecycle, not raw
        status. Two same-status items where one has expired differ in derived lifecycle, so they are
        not consolidated and the expired origin's state survives."""
        live = _mk("aaaaaaaa-0000-4000-8000-000000000001", "Live", "The default is A.",
                   created="2026-01-01T10:00:00Z")
        expired = _mk("bbbbbbbb-0000-4000-8000-000000000002", "Expired", "The default is A.",
                      created="2026-01-02T10:00:00Z", valid_until="2020-01-01")
        judg = {"equivalences": [{"uuids": [live["uuid"], expired["uuid"]], "meaning": "same"}]}
        doc = dc.compose(_bundle([live, expired]), focus=None, judgements=judg)
        self.assertEqual(doc.reconciliation["present"], 2)
        cats = [f["category"] for f in doc.findings]
        self.assertIn("equivalence-uncertain", cats)
        self.assertEqual(doc.lifecycle[expired["uuid"]], "no-longer-true")

    def test_overlapping_equivalence_groups_are_rejected(self):
        """reviewer finding 2: a uuid in two equivalence proposals is rejected fail-loud rather than
        rendered as two claim headers over one memory."""
        x = _mk("aaaaaaaa-0000-4000-8000-000000000001", "X", "same.")
        y = _mk("bbbbbbbb-0000-4000-8000-000000000002", "Y", "same.")
        z = _mk("cccccccc-0000-4000-8000-000000000003", "Z", "same.")
        judg = {"equivalences": [
            {"uuids": [x["uuid"], y["uuid"]], "meaning": "a"},
            {"uuids": [x["uuid"], z["uuid"]], "meaning": "b"},
        ]}
        with self.assertRaises(ValueError):
            dc.compose(_bundle([x, y, z]), focus=None, judgements=judg)

    def test_bundle_items_and_omitted_must_be_disjoint(self):
        """reviewer finding (validate_bundle): an item listed in both items and omitted is rejected
        rather than double-counted into present and omitted at once."""
        a = _mk("aaaaaaaa-0000-4000-8000-000000000001", "A", "x")
        bad = _bundle([a], omitted=[{"uuid": a["uuid"], "reason": "cap reached"}])
        with self.assertRaises(ValueError):
            dc.compose(bad, focus=None)

    def test_consolidated_no_source_origin_shows_unattributed(self):
        """reviewer finding (render): a consolidated group with an origin lacking recorded sources is
        shown as provenance-incomplete, not as distinct independent observations (no fabricated
        provenance)."""
        x = _mk("aaaaaaaa-0000-4000-8000-000000000001", "X", "same.", source_ref="SAME")
        y = _mk("bbbbbbbb-0000-4000-8000-000000000002", "Y", "same.", source_ref="SAME")
        y["sources"] = []
        judg = {"equivalences": [{"uuids": [x["uuid"], y["uuid"]], "meaning": "same"}]}
        doc = dc.compose(_bundle([x, y]), focus=None, judgements=judg)
        rendered = dc.render(doc)
        self.assertIn("provenance incomplete for at least one origin", rendered)
        self.assertNotIn("these are distinct sources", rendered)

    def test_per_focus_accounts_same_selected_and_carries_every_finding(self):
        """NFR-04 / LADR-12: every focus of one fixture accounts for the same selected memories and
        carries every finding the unfocused dossier carries."""
        bundle = _load("reconciliation_bundle.json")
        judg = {"equivalences": [{"uuids": [str(u) for u in (
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            "33333333-3333-3333-3333-333333333333")], "meaning": "same default rule"}]}
        selected_ids = {i["uuid"] for i in bundle["items"]}
        unfocused = dc.compose(bundle, focus=None, judgements=judg)
        unfocused_cats = {f["category"] for f in unfocused.findings}
        for focus in dc.FOCUSES:
            doc = dc.compose(bundle, focus=focus, judgements=judg)
            self.assertTrue(doc.reconciliation["closed"], focus)
            accounted = set()
            for c in doc.claims:
                if c.get("surfaced"):
                    accounted.update(o["uuid"] for o in c["origins"])
            accounted.update(o["uuid"] for o in doc.omitted)
            self.assertEqual(accounted, selected_ids, focus)
            focused_cats = {f["category"] for f in doc.findings}
            self.assertEqual(focused_cats, unfocused_cats, focus)
        # The review focus inverts the document: findings appear before the narrative (LADR-12).
        review_doc = dc.compose(bundle, focus="review", judgements=judg)
        rendered = dc.render(review_doc)
        self.assertLess(rendered.index("## Findings"), rendered.index("## Ordered claims"))

    def test_focus_is_single_valued_enum_and_unknown_focus_fails(self):
        """LADR-12: focus is a bounded enum; an unknown focus fails (no sixth focus)."""
        bundle = _bundle([_mk("aaaaaaaa-0000-4000-8000-000000000001", "A", "The default is A.")])
        with self.assertRaises(ValueError):
            dc.compose(bundle, focus="sixth")

    def test_omission_carries_bounded_reason_under_focus(self):
        """LADR-12: what a focus does not surface is listed omitted with ``outside-focus``."""
        items = [
            _mk("aaaaaaaa-0000-4000-8000-000000000001", "Arch", "The shape is a graph.",
                kind="architecture"),
        ]
        doc = dc.compose(_bundle(items), focus="requirements")
        self.assertEqual([o["reason"] for o in doc.omitted], ["outside-focus"])
        self.assertTrue(doc.reconciliation["closed"])


# ---------------------------------------------------------------------------- NFR-05 attribution


def _statement_lines(rendered):
    """Classify the rendered dossier's Ordered-claims section into substantive statement lines."""
    lines = rendered.splitlines()
    section = None
    stmt = []
    for line in lines:
        if line.startswith("## "):
            section = line[3:]
            continue
        if section != "Ordered claims":
            continue
        # A substantive statement line is a bullet carrying a claim. Exclude the provenance-missing
        # marker (which is a disclosure, not a substantive statement) and navigation/ordering lines.
        stripped = line.strip()
        if stripped.startswith("- ") and "_no recorded source or confidence_" not in stripped \
                and not stripped.startswith("> **analysis**"):
            stmt.append(stripped)
    return stmt


def _has_citation(line):
    return re.search(r"\[memory [0-9a-f\-]+ v\d+, captured [^\]]+\]", line) is not None


class Nfr05AttributionTests(unittest.TestCase):
    def test_zero_uncited_substantive_statements(self):
        """NFR-05: parse a produced dossier, classify statements, and assert the uncited-substantive
        count is zero."""
        bundle = _load("reconciliation_bundle.json")
        judg = {"equivalences": [{"uuids": [str(u) for u in (
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            "33333333-3333-3333-3333-333333333333")], "meaning": "same default rule"}]}
        for focus in list(dc.FOCUSES) + [None]:
            doc = dc.compose(bundle, focus=focus, judgements=judg)
            rendered = dc.render(doc)
            uncited = [l for l in _statement_lines(rendered) if not _has_citation(l)]
            self.assertEqual(uncited, [], f"uncited substantive statements under focus={focus}")

    def test_three_captures_collapsing_cite_all_three_origins(self):
        """NFR-05: a collapsed claim cites every origin, not the one the composition preferred."""
        items = [
            _mk("11111111-1111-1111-1111-111111111111", "A", "The default is A.", created="2026-01-01T10:00:00Z"),
            _mk("22222222-2222-2222-2222-222222222222", "B", "The default is A.", created="2026-01-02T10:00:00Z"),
            _mk("33333333-3333-3333-3333-333333333333", "C", "The default is A.", created="2026-01-03T10:00:00Z"),
        ]
        judg = {"equivalences": [{"uuids": [i["uuid"] for i in items], "meaning": "same"}]}
        doc = dc.compose(_bundle(items), focus=None, judgements=judg)
        rendered = dc.render(doc)
        for uuid in ("11111111-1111-1111-1111-111111111111",
                     "22222222-2222-2222-2222-222222222222",
                     "33333333-3333-3333-3333-333333333333"):
            self.assertIn(f"[memory {uuid} v1", rendered)
        # The origins are presented as re-captures of one source, not as corroboration (BR-23).
        self.assertIn("not independent corroboration", rendered)

    def test_composition_authored_text_marked_analysis_with_basis(self):
        """NFR-05: composition-authored text is marked **analysis** and states a basis; no stored
        claim carries the analysis marker."""
        items = [_mk("aaaaaaaa-0000-4000-8000-000000000001", "A", "The default is A.")]
        doc = dc.compose(_bundle(items), focus=None)
        rendered = dc.render(doc)
        self.assertIn("**analysis**", rendered)
        self.assertIn("Basis: LADR-07", rendered)
        # The stored claim line itself is a plain cited statement, not marked analysis.
        claim_lines = [l for l in _statement_lines(rendered)]
        self.assertTrue(claim_lines)
        for l in claim_lines:
            self.assertNotIn("**analysis**", l)

    def test_missing_provenance_is_visible(self):
        """NFR-05: a claim with no recorded source says so rather than presenting as attributed."""
        item = _mk("aaaaaaaa-0000-4000-8000-000000000001", "A", "The default is A.")
        item["sources"] = []
        doc = dc.compose(_bundle([item]), focus=None)
        rendered = dc.render(doc)
        self.assertIn("no recorded source or confidence", rendered)
        self.assertIn("unattributed", [f["category"] for f in doc.findings])

    def test_imperative_product_rule_stays_attributed_not_directive(self):
        """NFR-05 / BR-26: an imperative product rule appears attributed to its memory and not as a
        directive addressed to the reader."""
        item = _mk("aaaaaaaa-0000-4000-8000-000000000001", "Access rule",
                   "Only administrators may export the corpus.")
        doc = dc.compose(_bundle([item]), focus=None)
        rendered = dc.render(doc)
        # The imperative is retained verbatim but attributed, with the not-instructions banner.
        self.assertIn("Only administrators may export the corpus.", rendered)
        self.assertIn("[memory aaaaaaaa-0000-4000-8000-000000000001 v1", rendered)
        self.assertIn("not instructions to obey", rendered)
        # The condition survives composition (NFR-07): it is not compressed away.
        self.assertIn("Only administrators may export the corpus.", rendered)

    def test_superseded_and_stale_marked_at_point_of_use(self):
        """NFR-05 / NFR-07: superseded and stale items carry their status marker adjacent to the
        citation; a current item never does."""
        superseded = _mk("aaaaaaaa-0000-4000-8000-000000000001", "Old", "The default is A.",
                         status="superseded", created="2026-01-01T10:00:00Z")
        current = _mk("bbbbbbbb-0000-4000-8000-000000000002", "New", "The default is B.",
                      status="current", created="2026-02-01T10:00:00Z")
        edges = [{"sourceUuid": current["uuid"], "targetUuid": superseded["uuid"],
                  "relation": "supersedes", "reason": "replacement"}]
        doc = dc.compose(_bundle([superseded, current], edges), focus=None)
        self.assertEqual(doc.lifecycle[superseded["uuid"]], "superseded")
        self.assertEqual(doc.lifecycle[current["uuid"]], "current")
        rendered = dc.render(doc)
        self.assertIn("**Lifecycle:** superseded.", rendered)
        self.assertIn("**Lifecycle:** current.", rendered)


# ---------------------------------------------------------------------------- NFR-07 fidelity


class Nfr07FidelityTests(unittest.TestCase):
    def test_role_restricted_rule_and_customer_exception_survive(self):
        """NFR-07: a role-restricted rule and a customer-specific exception both survive composition."""
        items = [
            _mk("aaaaaaaa-0000-4000-8000-000000000001", "General", "The default is A.",
                source_ref="gen", created="2026-01-01T10:00:00Z"),
            _mk("bbbbbbbb-0000-4000-8000-000000000002", "Customer", "For Acme, the default is B (admin only).",
                scope=("customer", "acme"), source_ref="cust", created="2026-01-01T12:00:00Z"),
        ]
        doc = dc.compose(_bundle(items), focus=None)
        rendered = dc.render(doc)
        self.assertTrue(doc.reconciliation["closed"])
        self.assertIn("The default is A.", rendered)
        self.assertIn("For Acme, the default is B (admin only).", rendered)
        # The exception is not compressed into a general rule; the two stay distinct.
        self.assertEqual(len(doc.claims), 2)

    def test_copy_consolidation_reports_origins_not_corroboration(self):
        """NFR-07 / BR-23: copies consolidate with all origins listed and are not presented as
        independent corroboration."""
        items = [
            _mk("11111111-1111-1111-1111-111111111111", "A", "The default is A.", source_ref="SAME",
                created="2026-01-01T10:00:00Z"),
            _mk("22222222-2222-2222-2222-222222222222", "B", "The default is A.", source_ref="SAME",
                created="2026-01-02T10:00:00Z"),
        ]
        judg = {"equivalences": [{"uuids": [i["uuid"] for i in items], "meaning": "same"}]}
        doc = dc.compose(_bundle(items), focus=None, judgements=judg)
        rendered = dc.render(doc)
        self.assertEqual(doc.reconciliation["present"], 1)
        self.assertEqual(doc.reconciliation["consolidated"], 1)
        # Re-captures of one source are explicitly not corroboration.
        self.assertIn("re-capture one source", rendered)
        self.assertIn("not independent corroboration", rendered)

    def test_differing_customer_scope_claims_not_consolidated(self):
        """NFR-07: two claims sharing wording but differing in customer scope are not consolidated."""
        items = [
            _mk("aaaaaaaa-0000-4000-8000-000000000001", "Acme", "The default is A.",
                scope=("customer", "acme"), created="2026-01-01T10:00:00Z"),
            _mk("bbbbbbbb-0000-4000-8000-000000000002", "Beta", "The default is A.",
                scope=("customer", "beta"), created="2026-01-02T10:00:00Z"),
        ]
        judg = {"equivalences": [{"uuids": [i["uuid"] for i in items], "meaning": "same"}]}
        doc = dc.compose(_bundle(items), focus=None, judgements=judg)
        self.assertEqual(doc.reconciliation["present"], 2)
        self.assertEqual(doc.reconciliation["consolidated"], 0)
        # The uncertain equivalence is reported, keeping the distinction (LADR-05).
        self.assertIn("equivalence-uncertain", [f["category"] for f in doc.findings])

    def test_budget_exceeded_reports_omission_not_stripped_claim(self):
        """NFR-07: on a budget-exceeded fixture, conditions on included claims are intact and the
        shortfall appears as an omission, never as silently shortened claims."""
        items = [
            _mk("aaaaaaaa-0000-4000-8000-000000000001", "A", "The default is A (admin only)."),
            _mk("bbbbbbbb-0000-4000-8000-000000000002", "B", "For Acme, the default is B (admin only)."),
        ]
        # The deterministic side reports the cap as an omission on the second item.
        bundle_ = _bundle([items[0]], omitted=[{"uuid": items[1]["uuid"], "reason": "cap reached"}])
        doc = dc.compose(bundle_, focus=None)
        self.assertTrue(doc.reconciliation["closed"])
        rendered = dc.render(doc)
        self.assertIn("cap reached", rendered)
        # The included claim keeps its condition verbatim; it was not compressed away.
        self.assertIn("The default is A (admin only).", rendered)


# ---------------------------------------------------------------------------- NFR-06 read-only


class Nfr06CapabilityAbsenceTests(unittest.TestCase):
    def test_skill_exposes_no_store_write_operation(self):
        """NFR-06 / LADR-08: the skill exposes no write operation — read-only is structural."""
        module_attrs = [name for name in dir(dc) if not name.startswith("_")]
        # Any member that would write to the store (write / save / create-memory / set-memory /
        # persist). STORE_NAME is a constant for the sensitivity banner, not a write op; the
        # capability-absence test is about store write operations.
        write_ops = ("write", "savechanges", "save_changes", "create_memory", "set_memory",
                     "persist", "import_from_store", "store_memory", "create_link")
        write_like = [name for name in module_attrs
                      if any(tok in name.lower() for tok in write_ops)]
        self.assertEqual(write_like, [], f"module exposes store-write-like members: {write_like}")

    def test_cli_writes_only_the_local_artefact(self):
        """NFR-06: the only thing the CLI writes is the dossier artefact at the requested path; the
        composer has no path back into the store."""
        self.assertFalse(hasattr(dc, "import_from_store"))
        self.assertFalse(hasattr(dc, "save_changes"))
        # The CLI has a compose/bundle surface only; no subcommand writes to the store.
        self.assertTrue(hasattr(dc, "cmd_compose"))
        self.assertTrue(hasattr(dc, "cmd_bundle"))


class NearMissTagTests(unittest.TestCase):
    """LADR-10 / NFR-04: `near-miss-tag` is evidence-only skill output via the shared helper.
    No evidence means no finding; no tag-graph or store-wide completeness claim is made."""

    def _evidence(self):
        return {
            "scope": {"name": "Caller-approved product database evidence",
                      "authorization": "explicitly-supplied",
                      "records": [{"uuid": "aaaaaaaa-0000-4000-8000-000000000001", "version": 1,
                                   "scopeDimension": "product", "scopeIdentifier": None}]},
            "originalQuery": {"tags": ["postgres"], "facetMatchMode": "any", "scopeDimension": "product"},
            "records": [{"uuid": "aaaaaaaa-0000-4000-8000-000000000001", "version": 1,
                         "tags": ["database"], "status": "approved", "scopeDimension": "product",
                         "scopeIdentifier": None, "statement": "The product database uses PostgreSQL."}],
            "analyses": [{"uuid": "aaaaaaaa-0000-4000-8000-000000000001", "version": 1, "relevant": True,
                          "basis": {"classification": "analysis", "author": "skill",
                                    "explanation": "The supplied claim directly concerns PostgreSQL in the requested product scope.",
                                    "quote": "The product database uses PostgreSQL."}}],
            "selected": [], "disclosure": {},
        }

    def test_evidence_backed_mismatch_emits_a_finding_with_identity_and_basis(self):
        findings = dc.near_miss_findings(self._evidence())
        self.assertEqual(len(findings), 1)
        f = findings[0]
        self.assertEqual(f["category"], "near-miss-tag")
        self.assertEqual(f["classification"], "analysis")
        self.assertEqual(f["memories"][0]["uuid"], "aaaaaaaa-0000-4000-8000-000000000001")
        self.assertEqual(f["memories"][0]["version"], 1)
        self.assertTrue(f["basis"])
        self.assertTrue(f["scope"])

    def test_no_evidence_means_no_finding(self):
        """An unsupported plausible synonym (no grounded analysis) and an empty matched set emit no
        near-miss-tag finding (LADR-10)."""
        ev = self._evidence()
        # Relevant=False: the tag mismatch is not a finding without the skill's analysis.
        ev["analyses"][0]["relevant"] = False
        self.assertEqual(dc.near_miss_findings(ev), [])
        # No analyses (no evidence) at all => no finding.
        ev2 = self._evidence()
        ev2["analyses"] = []
        self.assertEqual(dc.near_miss_findings(ev2), [])

    def test_helper_is_the_reused_evidence_only_module(self):
        """The near-miss finding is delegated to the shared helper, which performs no search and makes
        no store-wide claim — this module does not extend into a tag graph."""
        # The helper validates and returns a qualification, not a store-wide assertion.
        import importlib.util
        path = dc._near_miss_helper_path()
        self.assertTrue(path.exists())
        self.assertIn("near_miss_tags", path.name)
        spec = importlib.util.spec_from_file_location("nm", path)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        report = mod.build_report(self._evidence())
        self.assertIn("not store-wide absence", report["qualification"])

    def test_near_miss_findings_feed_a_composed_dossier(self):
        """The skill feeds the helper's evidence-only findings into a composition; it carries a basis
        and a scope and the dossier still reconciles."""
        item = _mk("aaaaaaaa-0000-4000-8000-000000000001", "DB", "Uses PostgreSQL.", kind="architecture")
        bundle = _bundle([item])
        nm = dc.near_miss_findings(self._evidence())
        doc = dc.compose(bundle, focus=None, judgements={"findings": nm})
        self.assertTrue(doc.reconciliation["closed"])
        nm_findings = [f for f in doc.findings if f["category"] == "near-miss-tag"]
        self.assertEqual(len(nm_findings), 1)
        self.assertEqual(nm_findings[0]["classification"], "analysis")
        self.assertTrue(nm_findings[0]["basis"])
        self.assertTrue(nm_findings[0]["scope"])


if __name__ == "__main__":
    unittest.main(verbosity=2)



