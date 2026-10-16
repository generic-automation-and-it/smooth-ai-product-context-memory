---
name: ai-ralph-loop
description: 'Ralph loop over the current change: review, fix and verify, repeated with fresh eyes until a full pass finds nothing or the loop cap (default 10) is hit. Findings are grounded in known Understandings, *AGENTS.md, rules and specs, limited to feature gaps, potential bugs, architectural issues, YAGNI, KISS and clean code consistency, and the run ends with a table of every fix. Trigger on "/ai-ralph-loop", "ralph loop", "loop until no findings", "review and fix until clean". Not a one-shot review (use /code-review); never commits.'
effort: high  # multi-loop review, triage and fix judgment
---

# Ralph Loop — Skill

The same review-and-fix prompt, run again and again over one scope, each pass with fresh eyes, until a
full pass finds nothing. Progress lives in a ledger on disk, not in the conversation — that is what lets
every pass start clean and what stops a pass re-raising what an earlier one already settled.

## TL;DR

1. Bare `/ai-ralph-loop` runs the full loop: branch diff vs `origin/main`, up to 10 loops, stop at the first clean full-scope pass.
2. Only six categories count — see [Categories](#categories). Anything else is not a finding.
3. A finding without evidence is rejected. The [Evidence bar](#evidence-bar) is what makes the loop converge instead of running to the cap.
4. Every loop: review → triage → fix → verify → record. Loop 1 reviews the full scope; later loops review only what the last loop changed, then one full sweep confirms.
5. Ends with the [fix table](#output). Never commits or pushes.

## Switches

No switch is needed — a bare invocation is the full run.

| Switch | Effect | Default |
|--------|--------|---------|
| _(none)_ | Full run over the branch diff, up to 10 loops | — |
| `--loops <n>` | Loop cap; any positive integer | `10` |
| `--scope <path\|glob>` | Review these files instead of the branch diff | branch diff vs `origin/main` |
| `--spawn` | Run the two review lenses as parallel subagents with fresh context | off — both lenses run sequentially, inline, in the current agent pid |

Single-dash spellings (`-loops 5`) are accepted. An unrecognised switch or a non-positive `--loops` is
not guessed at: say what was passed and ask.

## Categories

| Category | Qualifies when | Not a finding |
|----------|----------------|---------------|
| Feature gap | A requirement **cited from a source** — worktask acceptance criterion, HLD, `*AGENTS.md` Requirements, Understanding, PR or issue body — is missing or only partly built | A feature nobody asked for (that is YAGNI) |
| Potential bug | A concrete input or state produces a wrong result, crash, data loss, race or leak | "Might fail" with no path to the failure |
| Architectural issue | Breaks a layer boundary, an ADR/LADR, or a rule (e.g. `backend/architecture-slices`) | Preferring another pattern |
| YAGNI | Code, abstraction, option or parameter with no current caller and no cited requirement | An extension point an ADR mandates |
| KISS | The same behaviour fits in clearly less code or indirection, with nothing lost | A rewrite to taste |
| Clean code consistency | Diverges from a rule (`clean-code`, `collection-types`, logging conventions, …) or from the idiom the surrounding code already uses | Formatting — `.editorconfig` owns it |

## Evidence bar

A finding carries all four, or it is rejected:

- **Category** — one of the six above.
- **Location** — `file:line`.
- **Evidence** — the failure scenario (input → wrong outcome), or the path of the rule, ADR, `*AGENTS.md`, Understanding or spec it breaks.
- **Fix** — one sentence.

On conflict between sources: rule > ADR/LADR > nearest `*AGENTS.md` > newest Understanding. The running
system (code plus passing tests) outranks an Understanding — a disagreement between them is reported,
never "fixed" by bending code toward the Understanding. Feature gap vs YAGNI: a cited requirement wins;
without a citation, YAGNI wins.

## Workflow

### Step 0 — Context pack (once per run)

1. **Scope.** Default:

   ```bash
   base=$(git merge-base origin/main HEAD)
   { git diff --name-only --diff-filter=d "$base"; git ls-files --others --exclude-standard; } | sort -u
   ```

   With `--scope`, expand the path or glob instead. Drop binaries, lock files and generated code
   (EF migration designers, snapshots). Empty scope → stop and say so.
2. **Known context**, loaded once and listed in the ledger so every reviewer reads the same pack:
   - `*AGENTS.md` chain for each scope file (`.agents/skills/context-load-agents-context/scripts/load-agents-context.sh --file <path>`, or walk ancestor directories).
   - Rules whose scope matches a scope file (index: `.agents/rules/AGENTS.md`).
   - `.context/understandings/INDEX.md` → every unit whose question matches the scope's subject. No store → continue without it.
   - Specs: active worktask under `.context/work-tasks/`, referenced `.docs/hlds/`, PR body (`gh pr view --json body`) when a PR exists.
3. **Baseline verify.** Build and run the tests covering the scope (commands from root `AGENTS.md`).
   Record failures as baseline — a loop is judged on *new* failures only.
4. **Open the ledger** (below).

### Ledger

`.context/ralph-loop/<yyyyMMdd-HHmm>/ledger.md` (UTC stamp, one folder per run). Header: scope list,
context pack paths, baseline verify result. Body:

```markdown
| ID | Loop | Category | File(s) | Status | Rationale |
```

`Status` is `fixed`, `rejected`, `deferred` or `oscillation`. Re-read the ledger at the start of every
loop — never rely on recall of earlier loops.

### Loop n (1 … cap)

1. **Review.** Target: loop 1 and any confirmation sweep = full scope; otherwise the **delta** — files
   edited in loop n−1 plus their direct callers and importers. Review through both lenses, each with
   the [Reviewer brief](#reviewer-brief):
   - Lens A: feature gap, potential bug.
   - Lens B: architectural issue, YAGNI, KISS, clean code consistency.

   Without `--spawn` (the default), run the two lenses **sequentially, inline, in the current agent
   pid**: lens A first over the target files, then lens B, re-reading the target files from disk
   between them so neither lens inherits the other's framing. With `--spawn`, run them as two
   parallel subagents, each handed the reviewer brief, the target file list, and the ledger path.
   A harness that cannot spawn subagents ignores `--spawn` and runs inline — never fail the run
   for it.
2. **Triage.** Check each finding against the code yourself. Accept, reject or defer it with a reason.
   A finding matching a ledger row (same file, same issue) is dropped.
3. **Fix.** Accepted findings only, in order: potential bug → feature gap → architectural issue → the
   rest. Smallest change that resolves it, matching the surrounding code. A bug fix gets a regression
   test in the tier where that code's tests already live. A fix that would leave the scope is `deferred`.
4. **Verify.** Build and run the tests for every touched project. A new failure → fix forward, at most
   two attempts; still red → stop the run, report the failing command, leave the tree as it is.
5. **Record.** One ledger row per finding, then a summary line:
   `Loop n: target=<full|delta k files> accepted a / rejected r / deferred d`.
6. **Exit check.**
   - 0 accepted on a full-scope pass → **converged**, stop.
   - 0 accepted on a delta pass → the next loop is a full-scope confirmation sweep.
   - n = cap → stop and report what is still open.

**Guards.** A fix that would undo an earlier loop's fix is rejected as `oscillation` — the earlier fix
stands and both are named in the report. A finding whose fix failed verification twice is `deferred`.

### Reviewer brief

Hand this section, the Categories table and the Evidence bar to each reviewer, with the target file
list and the ledger path.

- Read-only. Read the ledger first; never re-raise an item it lists.
- Read the context pack paths in the ledger header.
- Review only the target files; read other files only for evidence.
- Report only your lens's categories, and only findings that meet the Evidence bar.
- Reply with table rows `| Category | file:line | Evidence | Fix |`, most severe first, or the single word `none`.
- No praise, no summary, no formatting nits.

### Finish

1. When a fix changed behaviour a `*AGENTS.md` describes, update that file and its changelog.
2. When a loop surfaced knowledge worth keeping, suggest `/ai-understanding --export`.
3. Never commit or push.

## Output

The final message is this table — `fixed` rows only, IDs from the ledger:

```markdown
| ID | Category | File(s) fixed | Rationale | Loop |
|----|----------|---------------|-----------|------|
| RL-001 | Potential bug | `src/Project.Application/Features/Orders/CreateOrderHandler.cs` | Null customer id reached the repository; guard + regression test | 1 |
```

Then three lines:

- **Result** — `converged at loop k` · `cap reached (n loops)` · `stopped: verify red at loop k`.
- **Verify** — build/test commands and state, against baseline.
- **Not fixed** — rejected / deferred / oscillation counts, one line per deferred or oscillation item, and the ledger path.

No fixes at all → say `no findings` and give the Result and Verify lines only.
