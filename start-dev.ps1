<#
.SYNOPSIS
Startet den kompletten lokalen ctxman-Dev-Stack: Postgres, Mock-Backend, API, UI.

.DESCRIPTION
Zieht alles hoch, was die ctxman-ui zum Spielen braucht:
  1. Postgres-Container `ctxman-pg` (startet/erstellt ihn bei Bedarf, wartet auf ready)
  2. Mock-Backend (ctxman-ui/dev/mock-backend.mjs, Port 5999) — ersetzt Compaction-LLM
     und Promotion-Sink, damit Frame-Pop/Archive/Major-GC ohne echte Credentials laufen
  3. Ctxman.Api auf Port 5291, per Env auf das Mock-Backend gezeigt
  4. UI-Dev-Server (Vite, Port 5173, Proxy /api -> API)

Jeder Dienst läuft in einem eigenen PowerShell-Fenster (Logs sichtbar, einzeln beendbar).
Bereits belegte Ports werden erkannt und übersprungen — das Skript ist re-runnable.

.PARAMETER SkipDb
Postgres-Schritt überspringen (z. B. wenn die DB anderweitig läuft).

.PARAMETER NoMock
Mock-Backend nicht starten und die API ohne Compaction-Override starten —
dann müssen echte Credentials konfiguriert sein (Compaction:Anthropic:ApiKey),
sonst antworten Frame-Pop/Archive/Major-GC mit 500.

.PARAMETER NoBrowser
Browser am Ende nicht öffnen.

.PARAMETER Stop
Statt zu starten den kompletten Stack wieder herunterfahren: die Dienste auf
Mock-/API-/UI-Port (inkl. ihrer Log-Fenster) beenden und den Postgres-Container
ctxman-pg stoppen. Respektiert -SkipDb (DB läuft weiter) und -NoMock.

.EXAMPLE
./start-dev.ps1

.EXAMPLE
./start-dev.ps1 -Stop
#>
[CmdletBinding()]
param(
    [int]$ApiPort = 5291,
    [int]$UiPort = 5174,
    [int]$MockPort = 5999,
    [switch]$SkipDb,
    [switch]$NoMock,
    [switch]$NoBrowser,
    [switch]$Stop
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$ui = Join-Path $root "ctxman-ui"

function Test-PortInUse([int]$Port) {
    [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

function Stop-PortProcess([int]$Port, [string]$Label) {
    $conns = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if (-not $conns) {
        Write-Host "  -> ${Label} (Port ${Port}): nichts aktiv" -ForegroundColor DarkGray
        return
    }
    $pids = $conns | Select-Object -ExpandProperty OwningProcess -Unique
    foreach ($procId in $pids) {
        if (-not $procId) { continue }
        # Das umgebende pwsh-Log-Fenster mit-beenden, damit kein totes Fenster zurückbleibt.
        $killId = $procId
        $parentId = (Get-CimInstance Win32_Process -Filter "ProcessId = $procId" -ErrorAction SilentlyContinue).ParentProcessId
        if ($parentId) {
            $parent = Get-Process -Id $parentId -ErrorAction SilentlyContinue
            if ($parent -and $parent.ProcessName -in @("pwsh", "powershell")) { $killId = $parentId }
        }
        taskkill /PID $killId /T /F *> $null
        Write-Host "  -> ${Label} (Port ${Port}): PID ${killId} beendet" -ForegroundColor Green
    }
}

function Start-Window([string]$Title, [string]$Command, [string]$WorkingDirectory) {
    Start-Process pwsh -WorkingDirectory $WorkingDirectory -ArgumentList @(
        "-NoExit",
        "-Command",
        "`$Host.UI.RawUI.WindowTitle = '$Title'; $Command"
    ) | Out-Null
    Write-Host "  -> gestartet: $Title" -ForegroundColor Green
}

# --- Stop-Modus -------------------------------------------------------------
if ($Stop) {
    Write-Host "=== ctxman Dev-Stack stoppen ===" -ForegroundColor Cyan
    Stop-PortProcess $UiPort "UI"
    Stop-PortProcess $ApiPort "API"
    if (-not $NoMock) { Stop-PortProcess $MockPort "Mock-Backend" }

    if (-not $SkipDb) {
        Write-Host "Postgres (Container ctxman-pg)" -ForegroundColor Cyan
        $running = docker ps --filter "name=^ctxman-pg$" --format "{{.Names}}" 2>$null
        if ($running) {
            docker stop ctxman-pg | Out-Null
            Write-Host "  -> Container gestoppt" -ForegroundColor Green
        }
        else {
            Write-Host "  -> Container läuft nicht" -ForegroundColor DarkGray
        }
    }

    Write-Host ""
    Write-Host "Stack gestoppt." -ForegroundColor Cyan
    return
}

Write-Host "=== ctxman Dev-Stack ===" -ForegroundColor Cyan

# --- 1) Postgres ------------------------------------------------------------
if (-not $SkipDb) {
    Write-Host "[1/4] Postgres (Container ctxman-pg)" -ForegroundColor Cyan
    try { docker info *> $null } catch {}
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker ist nicht erreichbar — Docker Desktop starten und erneut versuchen (oder -SkipDb)."
    }

    $running = docker ps --filter "name=^ctxman-pg$" --format "{{.Names}}"
    if ($running) {
        Write-Host "  -> läuft bereits" -ForegroundColor Green
    }
    else {
        $exists = docker ps -a --filter "name=^ctxman-pg$" --format "{{.Names}}"
        if ($exists) {
            docker start ctxman-pg | Out-Null
            Write-Host "  -> vorhandenen Container gestartet" -ForegroundColor Green
        }
        else {
            docker run -d --name ctxman-pg -e POSTGRES_USER=ctxman -e POSTGRES_PASSWORD=ctxman `
                -e POSTGRES_DB=ctxman -p 5432:5432 postgres:16 | Out-Null
            Write-Host "  -> neuen Container erstellt" -ForegroundColor Green
        }
    }

    $tries = 0
    while ($tries -lt 30) {
        $ready = docker exec ctxman-pg pg_isready -U ctxman 2>$null
        if ($ready -match "accepting") { break }
        Start-Sleep 2
        $tries++
    }
    if ($tries -ge 30) { Write-Error "Postgres wurde nicht rechtzeitig bereit." }

    # Schema-Check: die API legt das Schema bewusst NICHT selbst an (kein Migrate/EnsureCreated,
    # keine eingecheckten Migrations) — ohne Tabellen schlägt jeder API-Call fehl.
    $hasSchema = docker exec ctxman-pg psql -U ctxman -d ctxman -tAc "SELECT to_regclass('public.sessions')" 2>$null
    if (-not ($hasSchema -match "sessions")) {
        Write-Warning ("Schema fehlt (Tabelle 'sessions' nicht gefunden). Einmalig anlegen, z. B. per " +
            "EnsureCreated-Tool (siehe ctxman-ui/README.md) oder EF-Migration. Die API startet trotzdem, " +
            "antwortet aber mit Fehlern, bis das Schema existiert.")
    }
    else {
        Write-Host "  -> Schema vorhanden" -ForegroundColor Green
    }
}

# --- 2) Mock-Backend ----------------------------------------------------------
if (-not $NoMock) {
    Write-Host "[2/4] Mock-Backend (Port $MockPort)" -ForegroundColor Cyan
    if (Test-PortInUse $MockPort) {
        Write-Host "  -> Port $MockPort belegt — läuft vermutlich schon" -ForegroundColor Yellow
    }
    else {
        Start-Window "ctxman mock-backend :$MockPort" "`$env:MOCK_PORT=$MockPort; node dev/mock-backend.mjs" $ui
    }
}
else {
    Write-Host "[2/4] Mock-Backend übersprungen (-NoMock)" -ForegroundColor Yellow
}

# --- 3) API -------------------------------------------------------------------
Write-Host "[3/4] Ctxman.Api (Port $ApiPort)" -ForegroundColor Cyan
if (Test-PortInUse $ApiPort) {
    Write-Host "  -> Port $ApiPort belegt — läuft vermutlich schon" -ForegroundColor Yellow
}
else {
    $apiEnv = if ($NoMock) { "" } else {
        "`$env:Compaction__Anthropic__BaseUrl='http://localhost:$MockPort'; `$env:Compaction__Anthropic__ApiKey='mock-key'; "
    }
    Start-Window "ctxman api :$ApiPort" `
        "$apiEnv dotnet run --project '$root\src\Ctxman.Api' --no-launch-profile --urls http://localhost:$ApiPort" `
        $root
}

# --- 4) UI --------------------------------------------------------------------
Write-Host "[4/4] UI (Vite, Port $UiPort)" -ForegroundColor Cyan
if (Test-PortInUse $UiPort) {
    Write-Host "  -> Port $UiPort belegt — läuft vermutlich schon" -ForegroundColor Yellow
}
else {
    if (-not (Test-Path (Join-Path $ui "node_modules"))) {
        Write-Host "  -> node_modules fehlt, npm install …" -ForegroundColor Yellow
        Push-Location $ui
        npm install
        Pop-Location
    }
    Start-Window "ctxman ui :$UiPort" "npm run dev" $ui
}

# --- Warten + Browser -----------------------------------------------------------
Write-Host "Warte auf API-Health…" -ForegroundColor Cyan
$tries = 0
while ($tries -lt 30) {
    try {
        $health = Invoke-RestMethod "http://localhost:$ApiPort/healthz" -TimeoutSec 2
        break
    }
    catch { Start-Sleep 2; $tries++ }
}
if ($health) {
    Write-Host "  -> API ok (auth: $($health.auth_mode))" -ForegroundColor Green
}
else {
    Write-Warning "API-Health nicht erreichbar — Log im API-Fenster prüfen."
}

Write-Host ""
Write-Host "ctxman-UI:  http://localhost:$UiPort" -ForegroundColor Cyan
Write-Host "API:        http://localhost:$ApiPort" -ForegroundColor Cyan
if (-not $NoMock) { Write-Host "Mock:       http://localhost:$MockPort" -ForegroundColor Cyan }

if (-not $NoBrowser) {
    Start-Process "http://localhost:$UiPort"
}
