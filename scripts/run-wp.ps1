# run-wp.ps1 — startet einen forge-Run für ein Workpaket.
#
# Verwendung (aus dem ctxman-Repo-Root):
#   .\scripts\run-wp.ps1 2                 # WP2 mit Default-Optionen, erzeugt PR
#   .\scripts\run-wp.ps1 2 -Plan           # nur Architekten-Plan (read-only, kein Code, kein PR)
#   .\scripts\run-wp.ps1 2 -DryRun         # MockAgent (keine claude-Calls) — schreibt nur Events
#   .\scripts\run-wp.ps1 3 -MaxIterations 3 -MaxTurns 200
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
    [int]$MaxIterations = 2,

    # Nur planen (architect, read-only) — kein Code, kein PR.
    [switch]$Plan,
    # MockAgent statt claude — Smoke-Test der Pipeline ohne Kosten.
    [switch]$DryRun,
    # PR-Erzeugung unterdrücken (Default: PR wird erzeugt).
    [switch]$NoPr
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$promptFile     = Join-Path $repoRoot "docs/forge-work/wp$Wp-prompt.md"
$acceptanceFile = Join-Path $repoRoot "docs/forge-work/wp$Wp-acceptance.md"

if (-not (Test-Path $promptFile)) {
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
    if ($DryRun) { $forgeArgs += "--dry-run" }
    if (-not $NoPr -and -not $DryRun) { $forgeArgs += "--create-pr" }

    uv run --project $ForgeDir forge @forgeArgs
}
finally {
    Pop-Location
}
