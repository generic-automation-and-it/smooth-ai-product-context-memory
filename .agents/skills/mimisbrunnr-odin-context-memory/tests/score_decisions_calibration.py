#!/usr/bin/env python3
"""Score the labelled calibration fixture for the export value gate.

This is ON-DEMAND evidence tooling, not a CI test: it needs a local decision model, so the CI
gate never runs it. What CI runs is `CalibrationEvidenceTests` in `test_decisions_gate.py`, which
holds the *shape* of the recorded run and refuses to let the rubric move away from it.

    # the blinded input: id + statement only. expect/tier/domain are withheld, because a
    # fixture that shows the model its own expected verdict scores nothing.
    python3 -B score_decisions_calibration.py --emit-model-input

    # score the shipped gate against the fixture and compare with the recorded run
    python3 -B score_decisions_calibration.py

Why the fixture exists at all: the gate's threshold and its rubric were both argued from
impression, and impression is what produced the claim that the model "saturates toward yes".
It does not. On a labelled junk-to-specific gradient the shipped gate scored precision 1.00 and
recall 1.00. Two attempts to tighten the rubric both made it worse. The numbers that say so live
in the fixture, so the next argument starts from them.
"""
import argparse
import json
import os
import pathlib
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
FIXTURE = HERE / "fixtures" / "decisions_calibration.json"
GATE = HERE.parent / "scripts" / "decisions_gate.py"


def load():
    with FIXTURE.open(encoding="utf-8") as handle:
        return json.load(handle)


def emit_model_input(doc):
    """Only what the model may see. `expect`, `tier` and `domain` are withheld."""
    out = [{"id": r["id"], "statement": r["statement"]} for r in doc["records"]]
    json.dump(out, sys.stdout, indent=2)
    sys.stdout.write("\n")


def run_gate(doc, threshold, endpoint, model):
    """Drive the real gate. A reimplementation of the scoring path would measure the copy."""
    env = dict(os.environ)
    env["CONTEXT_MEMORY_DECISIONS_ENABLED"] = "true"
    env["CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY"] = str(threshold)
    env["CONTEXT_MEMORY_DECISIONS_BASE_URL"] = endpoint
    env["CONTEXT_MEMORY_DECISIONS_MODEL"] = model
    payload = [{"subject": r["id"], "statement": r["statement"]} for r in doc["records"]]
    proc = subprocess.run(
        [sys.executable, "-B", str(GATE), "score"],
        input=json.dumps(payload), capture_output=True, text=True, env=env)
    if proc.returncode != 0:
        sys.stderr.write(proc.stdout + proc.stderr)
        raise SystemExit(f"the gate refused to score (exit {proc.returncode})")
    return json.loads(proc.stdout)


def score(doc, report, threshold):
    roles = list(doc["records"][0] and report["records"][0]["scores"])
    rows, tp, fp, tn, fn = [], 0, 0, 0, 0
    hit = bleed = pairs = 0
    for fixture, scored in zip(doc["records"], report["records"]):
        scores = scored.get("scores") or {}
        best = max(scores.values()) if scores else 0.0
        want = fixture["tier"] >= 2
        got = best > threshold
        tp += got and want
        fp += got and not want
        tn += (not got) and not want
        fn += (not got) and want
        if fixture["expect"]:
            pairs += len(fixture["expect"])
            hit += sum(1 for role in fixture["expect"] if scores.get(role, 0) > threshold)
            bleed += sum(1 for role, v in scores.items()
                         if v > threshold and role not in fixture["expect"])
        rows.append((fixture, scores, best, got, want))

    print(f"  rubric version {report['rubricVersion']}  model {report['model']}  "
          f"endpoint {report['endpoint']}  bar {threshold}\n")
    print(f"  {'id':<6}{'tier':>5}" + "".join(f"{r[:7]:>9s}" for r in roles) + f"{'verdict':>10}{'want':>7}")
    print("  " + "-" * (13 + 9 * len(roles) + 17))
    for fixture, scores, best, got, want in rows:
        mark = "" if got == want else ("  <- held" if want else "  <- passed")
        print(f"  {fixture['id']:<6}{fixture['tier']:>5}"
              + "".join(f"{scores.get(r, 0.0):9.2f}" for r in roles)
              + f"{('pass' if got else 'hold'):>10}{('pass' if want else 'hold'):>7}{mark}")

    prec = tp / (tp + fp) if tp + fp else 0.0
    rec = tp / (tp + fn) if tp + fn else 0.0
    print(f"\n  gate outcome      precision {prec:.2f}  recall {rec:.2f}  "
          f"(tp={tp} fp={fp} tn={tn} fn={fn})")
    print(f"  role attribution  expected clearing {hit}/{pairs}, unexpected clearing {bleed}")
    return dict(truePositive=tp, falsePositive=fp, trueNegative=tn, falseNegative=fn,
                precision=prec, recall=rec,
                expectedRolesClearing=hit, expectedRolesTotal=pairs,
                unexpectedRolesClearing=bleed)


def check_determinism(doc, threshold, endpoint, model, rounds=3):
    """Score the whole fixture `rounds` times and require byte-identical scores.

    Determinism is the property the whole calibration rests on: without it every figure carries
    sampling error, a threshold cannot be pinned, and a difference between two runs could not be
    told apart from a difference between two rubrics. It was measured by hand three times before it
    was written down, which is exactly the kind of claim that decays into folklore. This makes it
    re-runnable, and it fails loudly rather than reporting a mean over runs that disagreed.
    """
    runs = []
    for _ in range(rounds):
        report = run_gate(doc, threshold, endpoint, model)
        runs.append([r.get("scores") for r in report["records"]])
    agree = all(r == runs[0] for r in runs)
    print(f"  determinism over {rounds} full passes: "
          f"{'identical' if agree else 'DIFFERENT — every figure below is a mean over disagreeing runs'}")
    if not agree:
        for i, (a, b) in enumerate(zip(runs[0], runs[1])):
            if a != b:
                print(f"    first divergence at record {i}: {a} vs {b}")
    return agree


def compare(recorded, measured, threshold):
    """Say plainly whether this run reproduces the committed numbers."""
    print(f"\n  recorded run vs this run (bar {threshold})")
    drifted = []
    for key, got in measured.items():
        want = recorded.get(key)
        if want is None:
            continue
        ok = abs(got - want) < 0.005 if isinstance(want, float) else got == want
        print(f"    {key:<28}recorded {str(want):<8} measured {str(got):<8} "
              f"{'same' if ok else 'DIFFERENT'}")
        if not ok:
            drifted.append(key)
    if drifted:
        print("\n  This run does NOT reproduce the committed measurement. That is information, "
              "not a failure:\n  the model, the endpoint or the fixture may have moved. Re-record "
              "recordedRun deliberately, or fix the drift, and say which in the changelog.")
    else:
        print("\n  reproduces the committed measurement.")


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--emit-model-input", action="store_true",
                        help="print the blinded records (id + statement) and exit")
    parser.add_argument("--threshold", type=float, default=None,
                        help="override the bar (default: the fixture's recorded bar)")
    parser.add_argument("--endpoint", default="http://localhost:11434")
    parser.add_argument("--model", default="nimble")
    parser.add_argument("--check-determinism", action="store_true",
                        help="score the fixture three times and require identical scores (slow)")
    args = parser.parse_args()

    doc = load()
    if args.emit_model_input:
        emit_model_input(doc)
        return 0

    recorded = doc["recordedRun"]
    threshold = args.threshold if args.threshold is not None else recorded["bar"]
    print(f"Calibration fixture {FIXTURE.name}: {len(doc['records'])} labelled records, "
          f"pinned to rubric version {doc['rubricVersion']}\n")
    if args.check_determinism:
        if not check_determinism(doc, threshold, args.endpoint, args.model):
            print("\n  A non-deterministic model makes every threshold and every figure below "
                  "unreproducible. Re-run before trusting the comparison.")
            return 1
        print()
    report = run_gate(doc, threshold, args.endpoint, args.model)
    if report["rubricVersion"] != doc["rubricVersion"]:
        print(f"  WARNING: the shipped rubric is version {report['rubricVersion']} but the fixture "
              f"is pinned to {doc['rubricVersion']}. CalibrationEvidenceTests should already have "
              f"failed; these numbers describe a rubric the fixture was not written for.")
    measured = score(doc, report, threshold)
    compare(recorded, measured, threshold)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())