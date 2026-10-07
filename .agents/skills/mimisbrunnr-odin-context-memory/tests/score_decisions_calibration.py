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
import hashlib
import json
import os
import pathlib
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
FIXTURE = HERE / "fixtures" / "decisions_calibration.json"
GATE = HERE.parent / "scripts" / "decisions_gate.py"
RUBRIC = HERE.parent / "scripts" / "decisions_rubric.json"


def load():
    with FIXTURE.open(encoding="utf-8") as handle:
        return json.load(handle)


def rubric_sha256():
    return hashlib.sha256(RUBRIC.read_bytes()).hexdigest()


def emit_model_input(doc):
    """Only what the model may see. `expect`, `tier` and `domain` are withheld."""
    out = [{"id": r["id"], "statement": r["statement"]} for r in doc["records"]]
    json.dump(out, sys.stdout, indent=2)
    sys.stdout.write("\n")


def gate_environment(threshold, endpoint, model, base=None):
    """The gate's environment for a calibration run: what this run states, and nothing the operator set.

    The gate resolves every `CONTEXT_MEMORY_DECISIONS_*` setting from the environment and, at import,
    seeds the unset ones from the machine credential file. Inheriting either let the operator's own
    roles, request path, API key, below-threshold mode, timeout or attempt budget into a measurement
    that is compared with a committed run recorded without them — so a drifted figure could be the
    operator's configuration rather than the model (issue 184; issue 179 finding 41). Every decision
    setting is dropped, the credential-file pointer is redirected at an empty file rather than merely
    unset (an unset pointer falls back to `~/.mimisbrunnr/credentials`), and the run sets only the four
    it measures; the rest take the gate's shipped defaults, which is what the recorded run used.
    """
    env = {key: value for key, value in (os.environ if base is None else base).items()
           if not key.startswith("CONTEXT_MEMORY_DECISIONS_")}
    env["CONTEXT_MEMORY_CREDENTIAL_FILE"] = os.devnull
    env["CONTEXT_MEMORY_DECISIONS_ENABLED"] = "true"
    env["CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY"] = str(threshold)
    env["CONTEXT_MEMORY_DECISIONS_BASE_URL"] = endpoint
    env["CONTEXT_MEMORY_DECISIONS_MODEL"] = model
    return env


def run_gate(doc, threshold, endpoint, model):
    """Drive the real gate. A reimplementation of the scoring path would measure the copy."""
    env = gate_environment(threshold, endpoint, model)
    payload = [{"subject": r["id"], "statement": r["statement"]} for r in doc["records"]]
    proc = subprocess.run(
        [sys.executable, "-B", str(GATE), "score"],
        input=json.dumps(payload), capture_output=True, text=True, env=env)
    if proc.returncode != 0:
        sys.stderr.write(proc.stdout + proc.stderr)
        raise SystemExit(f"the gate refused to score (exit {proc.returncode})")
    return json.loads(proc.stdout)


def require_scored(doc, report):
    """Refuse a report in which any fixture record was not scored, or cannot be paired to its record.

    A record the gate did not score — `unreachable`, `timed-out`, `bad-response`, `oversize` — carries
    no scores, and reading that as a best-role of 0.0 counted it as a hold: a true negative on the
    hold side and a false negative on the pass side, with nothing saying it was never measured. That
    is the gate's own rule (a failed round is never a low score) broken by the tool that measures it.
    Records pair by the id sent as `subject`, so a reordered or truncated report cannot misalign them.
    """
    records = report.get("records")
    if not isinstance(records, list) or len(records) != len(doc["records"]):
        raise SystemExit(f"the gate reported {len(records) if isinstance(records, list) else 'no'} "
                         f"record(s) for {len(doc['records'])} fixture record(s); refusing to score")
    for fixture, scored in zip(doc["records"], records):
        if scored.get("identity") != fixture["id"]:
            raise SystemExit(f"calibration record {fixture['id']} was paired with "
                             f"{scored.get('identity')!r}; refusing to score")
        if scored.get("outcome") != "scored" or not scored.get("scores"):
            raise SystemExit(f"calibration record {fixture['id']} was not scored "
                             f"({scored.get('outcome')!r}); every figure would count it as a hold")


def mean(values):
    return sum(values) / len(values) if values else 0.0


def pearson(xs, ys):
    """Pearson's r, or None when either side is constant and r is undefined."""
    mx, my = mean(xs), mean(ys)
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    sxx = sum((x - mx) ** 2 for x in xs)
    syy = sum((y - my) ** 2 for y in ys)
    if not sxx or not syy:
        return None
    return sxy / (sxx * syy) ** 0.5


def distribution(doc, report, roles, threshold):
    """The shape of the scores, not only the verdicts they produce.

    The confusion matrix and role counts are verdict-level, so a run whose hold side climbed from 0.2
    to 0.84 and whose pass side fell from 0.96 to 0.86 reproduced every one of them at a 0.85 bar
    while the bimodal gap the threshold rests on had closed (issue 182). These are the figures the
    fixture records about that gap; measuring them is what lets `compare` notice it moving.
    """
    hold, keep = [], []
    clearing = {role: 0 for role in roles}
    for fixture, scored in zip(doc["records"], report["records"]):
        scores = scored["scores"]
        (keep if fixture["tier"] >= 2 else hold).append(max(scores.values()))
        for role in roles:
            clearing[role] += scores.get(role, 0.0) > threshold
    best_pair, best_r = None, None
    columns = {role: [r["scores"].get(role, 0.0) for r in report["records"]] for role in roles}
    for i, first in enumerate(roles):
        for second in roles[i + 1:]:
            r = pearson(columns[first], columns[second])
            if r is not None and (best_r is None or r > best_r):
                best_pair, best_r = f"{first}/{second}", r
    return {
        "tier01MeanBestRole": round(mean(hold), 3),
        "tier01MaxBestRole": round(max(hold), 3) if hold else 0.0,
        "tier23MeanBestRole": round(mean(keep), 3),
        "tier23MinBestRole": round(min(keep), 3) if keep else 0.0,
        "separation": round(mean(keep) - mean(hold), 3),
        "perRoleClearing": clearing,
        "maxCrossRoleCorrelation": None if best_r is None else round(best_r, 3),
        "maxCrossRolePair": best_pair,
    }


def score(doc, report, threshold):
    require_scored(doc, report)
    roles = list(report["records"][0]["scores"])
    rows, tp, fp, tn, fn = [], 0, 0, 0, 0
    hit = bleed = pairs = 0
    for fixture, scored in zip(doc["records"], report["records"]):
        scores = scored["scores"]
        best = max(scores.values())
        want = fixture["tier"] >= 2
        got = best > threshold
        tp += got and want
        fp += got and not want
        tn += (not got) and not want
        fn += (not got) and want
        if fixture["expect"]:
            pairs += len(fixture["expect"])
            hit += sum(1 for role in fixture["expect"] if scores.get(role, 0) > threshold)
        # Every role that clears without being expected is bleed, a hold-side record (no expected role)
        # included: counting only records with an expectation hid exactly the role clearing on junk
        # (consumer review 5440964552 #5). The recorded runs hold no false positive, so their figures
        # are unchanged.
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
    shape = distribution(doc, report, roles, threshold)
    print(f"  score shape       hold-side mean {shape['tier01MeanBestRole']:.3f} "
          f"max {shape['tier01MaxBestRole']:.3f}, pass-side mean {shape['tier23MeanBestRole']:.3f} "
          f"min {shape['tier23MinBestRole']:.3f}, separation {shape['separation']:.3f}")
    return dict(truePositive=tp, falsePositive=fp, trueNegative=tn, falseNegative=fn,
                precision=prec, recall=rec,
                expectedRolesClearing=hit, expectedRolesTotal=pairs,
                unexpectedRolesClearing=bleed, **shape)


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
        require_scored(doc, report)
        runs.append([{"scores": r.get("scores"), "outcome": r.get("outcome"), "passed": r.get("passed")}
                     for r in report["records"]])
    divergences = first_divergences(runs)
    agree = not divergences
    print(f"  determinism over {rounds} full passes: "
          f"{'identical' if agree else 'DIFFERENT — every figure below is a mean over disagreeing runs'}")
    for line in divergences:
        print(f"    {line}")
    return agree


def first_divergences(runs):
    """For every pass after the first, where it first disagrees with pass 1 — or nothing when all agree.

    Each later pass is compared with the first, on every record's scores, outcome and pass flag. Only
    pass 2 used to be reported, so a disagreement that appeared in pass 3 failed the check with no line
    saying where (issue 186); a different record count is a divergence too, not a shorter comparison.
    """
    lines = []
    for number, run in enumerate(runs[1:], start=2):
        if len(run) != len(runs[0]):
            lines.append(f"pass {number} scored {len(run)} record(s), pass 1 scored {len(runs[0])}")
            continue
        for index, (first, later) in enumerate(zip(runs[0], run)):
            if first != later:
                lines.append(f"pass {number} first diverges from pass 1 at record {index}: "
                             f"{first} vs {later}")
                break
    return lines


# Every figure a recorded run may carry. A key listed here that the recorded run holds and this run did
# not measure is drift, never a skip: the previous `continue` on an unmeasured key is how the
# distribution figures went unchecked while the comparison printed "reproduces" (issue 182).
COMPARED_KEYS = ("truePositive", "falsePositive", "trueNegative", "falseNegative",
                 "precision", "recall", "expectedRolesClearing", "expectedRolesTotal",
                 "unexpectedRolesClearing", "tier01MeanBestRole", "tier01MaxBestRole",
                 "tier23MeanBestRole", "tier23MinBestRole", "separation", "perRoleClearing",
                 "maxCrossRoleCorrelation", "maxCrossRolePair")


def recorded_at(recorded, threshold):
    """The recorded block measured at this bar: the primary run, or a nested `atBar…` run."""
    candidates = [recorded] + [value for key, value in recorded.items()
                               if key.startswith("atBar") and isinstance(value, dict)]
    for candidate in candidates:
        if isinstance(candidate.get("bar"), (int, float)) and abs(candidate["bar"] - threshold) < 1e-9:
            return candidate
    return None


def compare(recorded, measured, threshold, report=None):
    """Say plainly whether this run reproduces the committed numbers. Returns the drifted keys."""
    print(f"\n  recorded run vs this run (bar {threshold})")
    block = recorded_at(recorded, threshold)
    if block is None:
        print(f"    no recorded run at bar {threshold}; a different bar measures a different gate, so "
              "nothing here can be compared with the committed numbers.")
        return ["bar"]
    drifted = []
    if report is not None:
        for key in ("model", "endpoint"):
            if key in recorded and report.get(key) != recorded[key]:
                print(f"    {key:<28}recorded {recorded[key]!s:<8} measured {report.get(key)!s:<8} DIFFERENT")
                drifted.append(key)
    for key in COMPARED_KEYS:
        if key not in block:
            continue
        want, got = block[key], measured.get(key)
        if got is None:
            # A measured `None` is drift unless the key was measured and the recorded run is `None` too:
            # Pearson is undefined for constant scores, and two undefined correlations agree (consumer
            # review 5441621898 #9). An unmeasured key stays drift (issue 182).
            ok = want is None and key in measured
        elif isinstance(want, float) or isinstance(got, float):
            ok = abs(got - want) < 0.005
        else:
            ok = got == want
        print(f"    {key:<28}recorded {str(want):<8} measured {str(got):<8} "
              f"{'same' if ok else ('NOT MEASURED' if got is None else 'DIFFERENT')}")
        if not ok:
            drifted.append(key)
    if drifted:
        print("\n  This run does NOT reproduce the committed measurement. That is information, "
              "not a failure:\n  the model, the endpoint or the fixture may have moved. Re-record "
              "recordedRun deliberately, or fix the drift, and say which in the changelog.")
    else:
        print("\n  reproduces the committed measurement.")
    return drifted


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
    if rubric_sha256() != doc["rubricSha256"]:
        print("  WARNING: the shipped rubric's bytes differ from the rubric the fixture was measured "
              "against, whatever its version says. Re-record recordedRun and rubricSha256 together.")
    measured = score(doc, report, threshold)
    compare(recorded, measured, threshold, report)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())