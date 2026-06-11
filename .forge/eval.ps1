# forge Eval-Suite "quick" für ctxman.
#
# Kontrakt (forge parses: scores_json): die LETZTE {...}-Gruppe auf stdout ist ein
# JSON-Dict mit Float-Werten. Keys müssen den GateKinds aus .forge/project.yaml
# entsprechen: build_success, pytest_pass_rate (trägt die dotnet-test-Pass-Rate),
# test_count. Exit-Code 0 nur wenn Build ok UND alle Tests grün.

$ErrorActionPreference = "Continue"

function Emit-Scores([double]$build, [double]$passRate, [double]$testCount) {
    # -Compress hält das JSON in einer Zeile — robust für forges {…}-Suche.
    @{ build_success = $build; pytest_pass_rate = $passRate; test_count = $testCount } |
        ConvertTo-Json -Compress
}

# --- 1. Build ---------------------------------------------------------------
dotnet build ctxman.sln -clp:ErrorsOnly --nologo
if ($LASTEXITCODE -ne 0) {
    Emit-Scores 0.0 0.0 0.0
    exit 1
}

# --- 2. Tests (TRX-Logger, damit die Zählung parsebar ist) -------------------
$trxDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ctxman-eval-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $trxDir | Out-Null

dotnet test ctxman.sln --no-build --nologo --logger "trx;LogFileName=results.trx" --results-directory $trxDir
$testExit = $LASTEXITCODE

$trx = Get-ChildItem -Path $trxDir -Filter *.trx -Recurse | Select-Object -First 1
if ($null -eq $trx) {
    # Kein TRX entstanden (z. B. Test-Host-Crash) — fail-closed.
    Emit-Scores 1.0 0.0 0.0
    exit 1
}

[xml]$doc = Get-Content $trx.FullName
$counters = $doc.TestRun.ResultSummary.Counters
$total  = [int]$counters.total
$passed = [int]$counters.passed

Remove-Item -Recurse -Force $trxDir -ErrorAction SilentlyContinue

if ($total -eq 0) {
    # Keine Tests gefunden: vacuously green wäre gefährlich — das Skeleton hat
    # immer mindestens einen Test. 0 Tests heißt: etwas ist kaputt.
    Emit-Scores 1.0 0.0 0.0
    exit 1
}

$passRate = [math]::Round($passed / $total, 6)
Emit-Scores 1.0 $passRate ([double]$total)

if ($testExit -ne 0 -or $passed -lt $total) { exit 1 }
exit 0
