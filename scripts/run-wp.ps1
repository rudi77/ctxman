# run-wp.ps1 — startet einen forge-Run für ein Workpaket.
#
# Verwendung (aus dem ctxman-Repo-Root):
#   .\scripts\run-wp.ps1 2                 # WP2 mit Default-Optionen, erzeugt PR
#   .\scripts\run-wp.ps1 2 -Plan           # nur Architekten-Plan (read-only, kein Code, kein PR)
#   .\scripts\run-wp.ps1 2 -DryRun         # MockAgent (keine claude-Calls) — schreibt nur Events
#   .\scripts\run-wp.ps1 3 -MaxIterations 3 -MaxTurns 200
#   .\scripts\run-wp.ps1 7 -Resume 01KT... # einen vom Session-Limit unterbrochenen Run
#                                          # fortsetzen (claude --resume im selben Worktree).
#                                          # Die run_id steht im "run summary" / forge watch des
#                                          # abgebrochenen Laufs (decision: rate_limited).
#
# Voraussetzungen: `claude` ist via `claude /login` angemeldet (Subscription),
# `gh` ist authentifiziert, .NET 9 SDK installiert.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [int]$Wp,

    # Pfad zum forge-Quellbaum (uv-Projekt).
    [string]$ForgeDir = "C:\Users\rudi\source\forge",

    [string]$Model = "opus",
    [string]$Agents = "architect,developer,tester,reviewer",
    [int]$MaxTurns = 150,
    [int]$MaxIterations = 1,

    # Nur planen (architect, read-only) — kein Code, kein PR.
    [switch]$Plan,
    # MockAgent statt claude — Smoke-Test der Pipeline ohne Kosten.
    [switch]$DryRun,
    # PR-Erzeugung unterdrücken (Default: PR wird erzeugt).
    [switch]$NoPr,
    # run_id eines vom Session-/Usage-Limit unterbrochenen Runs fortsetzen.
    # Lädt den Resume-Anker aus dem Event-Store und dockt via `claude --resume`
    # an den eingefrorenen Worktree an. Prompt-Datei wird dann NICHT gebraucht.
    [string]$Resume = "",

    # claude session_id explizit (für Runs OHNE Anker, z.B. vor dem Feature
    # abgebrochen). Steht im stream-json-Log unter .forge/logs/<run_id>/.
    [string]$ResumeSessionId = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$promptFile     = Join-Path $repoRoot "docs/forge-work/wp$Wp-prompt.md"
$acceptanceFile = Join-Path $repoRoot "docs/forge-work/wp$Wp-acceptance.md"

if (-not $Resume -and -not (Test-Path $promptFile)) {
    throw "Prompt-Datei nicht gefunden: $promptFile"
}

Push-Location $repoRoot
try {
    if ($Plan) {
        # Architekten-Plan zum Review, bevor Code geschrieben wird.
        $out = Join-Path $repoRoot "wp$Wp-plan-review.md"
        uv run --project $ForgeDir forge plan (Get-Content $promptFile -Raw) `
            --model $Model -o $out
        Write-Host "Plan geschrieben nach: $out" -ForegroundColor Green
        return
    }

    if ($Resume) {
        # Resume: kein Prompt nötig (die CLI nutzt eine Continue-Anweisung),
        # der Runner dockt an den eingefrorenen Worktree des Runs an.
        $forgeArgs = @(
            "run",
            "--resume", $Resume,
            "--agents", $Agents,
            "--model", $Model,
            "--max-turns", $MaxTurns,
            "--max-iterations", $MaxIterations,
            "--eval-suite", "quick"
        )
        if ($ResumeSessionId) { $forgeArgs += @("--resume-session-id", $ResumeSessionId) }
    }
    else {
        $forgeArgs = @(
            "run",
            "--prompt-file", $promptFile,
            "--acceptance-file", $acceptanceFile,
            "--agents", $Agents,
            "--model", $Model,
            "--max-turns", $MaxTurns,
            "--max-iterations", $MaxIterations,
            "--eval-suite", "quick"
        )
    }
    if ($DryRun) { $forgeArgs += "--dry-run" }
    if (-not $NoPr -and -not $DryRun) { $forgeArgs += "--create-pr" }

    uv run --project $ForgeDir forge @forgeArgs
}
finally {
    Pop-Location
}
