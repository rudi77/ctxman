"""Recovery-Tool: lässt forges LLM-Judge nachträglich gegen einen vorhandenen
Run-Worktree laufen.

Use-case: ein Multi-Agent-Run wurde vom Wallclock-Timeout gekappt, NACHDEM der
Developer fertig war, aber BEVOR Eval/Judge liefen. Die Arbeit liegt grün im
Worktree. Dieses Skript reproduziert exakt den Judge-Call der Pipeline
(forge_execute.evaluators.judge.JudgeEvaluator) gegen den Worktree-Diff —
ohne den Developer neu laufen zu lassen.

Aufruf (aus dem ctxman-Repo, via forge-venv):
    uv run --project C:\\Users\\rudi\\source\\forge python `
        scripts/judge-worktree.py <run_id> docs/forge-work/wpN-acceptance.md

Exit 0 = Judge pass (Score >= Gate-Schwelle), Exit 1 = fail.
"""

from __future__ import annotations

import sys
from pathlib import Path

from forge_core.spec import load_spec
from forge_execute.agents.claude_cli import (
    ClaudeCodeCLIAgent,
    _git,
    _stage_untracked_for_diff,
)
from forge_execute.evaluators.judge import JudgeEvaluator


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: judge-worktree.py <run_id> <acceptance-file>", file=sys.stderr)
        return 2

    run_id, acceptance_file = sys.argv[1], sys.argv[2]
    repo_root = Path.cwd()
    worktree = repo_root / ".forge" / "worktrees" / run_id
    if not worktree.is_dir():
        print(f"worktree nicht gefunden: {worktree}", file=sys.stderr)
        return 2

    spec = load_spec(repo_root / ".forge" / "project.yaml")
    judge_cfg = spec.judge
    threshold = next(
        (g.threshold for g in spec.gates if g.kind == "llm_judge_score"),
        judge_cfg.threshold,
    )

    # Diff exakt wie der propose-Pfad bauen: untracked sichtbar machen
    # (.claude ausgeschlossen), dann Working Tree gegen den Worktree-Basis-Commit.
    base_sha = _git(worktree, "rev-parse", "HEAD")
    _stage_untracked_for_diff(worktree)
    diff = _git(worktree, "diff", base_sha)
    if not diff.strip():
        print("leerer Diff — nichts zu bewerten", file=sys.stderr)
        return 2

    acceptance = Path(acceptance_file).read_text(encoding="utf-8")

    agent = ClaudeCodeCLIAgent(default_model=judge_cfg.model, agents=["developer"])
    judge = JudgeEvaluator(agent)

    print(
        f"Judge läuft (model={judge_cfg.model}, max_turns={judge_cfg.max_turns}, "
        f"Schwelle={threshold}) gegen {len(diff)} Zeichen Diff …",
        file=sys.stderr,
    )
    outcome = judge.run(
        worktree=worktree,
        acceptance_criteria=acceptance,
        diff=diff,
        max_turns=judge_cfg.max_turns,
        budget_usd=spec.cost_caps.per_generation_usd,
        model=judge_cfg.model,
    )

    print("\n" + "=" * 70)
    print(f"VERDICT: {outcome.verdict}")
    print(f"SCORE:   {outcome.score}  (Schwelle {threshold})")
    print(f"COST:    ${outcome.cost_usd}")
    if outcome.error:
        print(f"ERROR:   {outcome.error}")
    print("=" * 70)
    print("\nREASONING:\n" + (outcome.reasoning or "(keine)"))

    return 0 if outcome.score >= threshold else 1


if __name__ == "__main__":
    raise SystemExit(main())
