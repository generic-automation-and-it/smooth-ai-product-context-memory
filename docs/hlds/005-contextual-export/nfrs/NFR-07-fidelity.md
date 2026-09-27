# NFR-07: Fidelity

**Status:** Draft

## Requirement

Composition must not change what a claim applies to. Concision is allowed; losing an applicability
condition is not.

- **Every** definition, condition, threshold, permission and exception needed to interpret a selected claim survives composition. A summary that drops a customer-specific exception has produced a false claim, not a shorter one.
- Where paraphrasing would change meaning, the **recorded wording is retained verbatim**.
- **Lifecycle survives**: current, proposed, superseded and no-longer-true remain distinguishable, and unknown status is stated as unknown rather than omitted or inferred. Capture recency is never treated as evidence that behaviour shipped.
- **Consolidation preserves distinction.** Two claims are equivalent only when meaning, applicability *and* lifecycle match. Similar wording is not equivalence, and where equivalence is uncertain the distinction is kept.
- **Repeated captures of one source are not independent corroboration.** Multiple origins are reported as what they are — several captures, distinguishing independent observation from a copied source — never counted as strength.
- **A budget never wins against applicability.** When a size or cost limit is reached, the export narrows what it *includes* and reports the omission; it never keeps a claim while compressing away the condition that bounds it.

## Verification

- **Reference-example review** — the product owner's representative examples (spec preparation, handover, re-entry) have their critical rules, conditions and exceptions identified *before* the export is produced. Assert every one survives composition. This is the primary verification and it is a review, not an automated check: fidelity is a semantic property.
- **Skill-level test, invitation-style fixture** — a slice holding a role-restricted rule, a customer-specific expiry exception, a proposed change to a default, a superseded older default, and equivalent copies of the rule. Assert: the restriction and the scoped exception both survive; the copies consolidate with all origins listed; the proposed change is labelled and not presented as shipped; the superseded default remains readable with its recorded replacement.
- **Skill-level test** — a fixture where two claims share wording but differ in customer scope. Assert they are not consolidated.
- **Skill-level test** — a fixture where three origins are re-captures of one source. Assert the document does not present them as independent corroboration.
- **Skill-level test** — a fixture that exceeds the composition budget. Assert conditions and exceptions on included claims are intact and the shortfall appears as an omission, not as silently shortened claims.
- **Skill-level test** — a `kind = understanding` fixture whose claim carries a scoped condition or exception. Assert it survives composition verbatim where paraphrase would change meaning, and that the kind is not treated as less fidelity-critical than stored product claims (LADR-15).

## Acceptance Criteria

- Every critical rule, condition and exception identified in the reviewed examples survives composition.
- Verbatim wording is retained wherever paraphrase would alter applicability.
- Proposed, superseded and no-longer-true material is distinguishable from current, and unknown status is stated.
- No consolidation merges claims differing in meaning, applicability or lifecycle; uncertain equivalence keeps the distinction.
- Multiple origins are never presented as corroboration without distinguishing independent captures from copies of one source.
- Reaching a budget produces a reported omission, never a claim stripped of its conditions.
- The fidelity bar applies without distinction to `kind = understanding` items (LADR-15).

## Applies To

Goal 2 (a composed document). LADR-05 (consolidation), LADR-12 (a focus changes depth, so it is the
most likely route to a lost condition). `BR-22`, `BR-23`, `BR-24`, and `BR-32`'s "essential conditions
are not compressed away to fit". The success measure **Detail fidelity** is this NFR seen from the
business side.
