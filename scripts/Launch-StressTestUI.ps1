<#
.SYNOPSIS
Launches the AgentIsland main application with real UI, wired to an isolated 1-year worst-case test dataset.
.DESCRIPTION
1. Ensures no stale AgentIsland instance is running.
2. Builds AgentIsland with latest changes.
3. Creates an isolated temporary directory with 365 days of Claude and Codex session history (730+ files).
4. Maps IslandPaths environment variables (CLAUDE_CONFIG_DIR, CLAUDE_DESKTOP_DIR, CODEX_HOME, AGENTISLAND_DATA_DIR) to the fixture.
5. Launches AgentIsland.exe with live top bar / Dynamic Island UI and opens the Settings/Usage Inspector.
#>
param(
    [switch]$NoRebuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$fixtureDir = Join-Path ([System.IO.Path]::GetTempPath()) "AgentIsland_StressUIFixture"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  AgentIsland: Launching UI with 1-Year Worst-Case Fixture" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Kill any existing AgentIsland process so mutex and file locks are completely clean
Get-Process -Name AgentIsland -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "[1/4] Stopping lingering AgentIsland process (PID: $($_.Id))..." -ForegroundColor Yellow
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 300

# 2. Build AgentIsland
$exePath = Join-Path $repoRoot "src\AgentIsland\bin\Debug\net8.0-windows\AgentIsland.exe"
if (-not $NoRebuild -or -not (Test-Path $exePath)) {
    Write-Host "[2/4] Building AgentIsland..." -ForegroundColor Yellow
    dotnet build (Join-Path $repoRoot "src\AgentIsland\AgentIsland.csproj") -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed."
        exit 1
    }
}

# 3. Generate 1-Year (365 days) dataset if not already present
$claudeProjectDir = Join-Path $fixtureDir "claude-config\projects\app-repo"
$claudeDesktopDir = Join-Path $fixtureDir "claude-desktop"
$codexSessionsDir = Join-Path $fixtureDir "codex-home\sessions"
$dataDir = Join-Path $fixtureDir "island-data"

New-Item -ItemType Directory -Force -Path $claudeProjectDir | Out-Null
New-Item -ItemType Directory -Force -Path $claudeDesktopDir | Out-Null
New-Item -ItemType Directory -Force -Path $codexSessionsDir | Out-Null
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

$settingsFile = Join-Path $dataDir "settings.json"
$settingsContent = @"
{
  "AgentIsland.providerOrder.v1": [
    "claude",
    "codex",
    "antigravity",
    "grok",
    "cursor",
    "deepseek"
  ],
  "AgentIsland.enabledProviders.v1": [
    "claude",
    "codex"
  ],
  "AgentIsland.claudeVisible": true,
  "AgentIsland.codexVisible": true,
  "AgentIsland.quotaDisplayMode": "Remaining",
  "AgentIsland.lowPowerMode": false,
  "AgentIsland.alwaysShowUsage": true,
  "AgentIsland.weeklyReportShownForWeek": "2026-W36"
}
"@
[System.IO.File]::WriteAllText($settingsFile, $settingsContent)

if (-not (Test-Path $claudeProjectDir) -or (Get-ChildItem $claudeProjectDir -Filter "*.jsonl").Count -lt 300) {
    Write-Host "[3/4] Generating 365 days of session history for Claude + Codex in $fixtureDir..." -ForegroundColor Yellow
    $now = [DateTimeOffset]::UtcNow
    for ($day = 365; $day -ge 0; $day--) {
        $fileDate = $now.AddDays(-$day).AddHours($day % 24)

        # Claude file
        $claudeSid = "claude-sess-{0:D4}" -f $day
        $claudeFile = Join-Path $claudeProjectDir "$claudeSid.jsonl"
        $statusLine = if ($day -eq 0) { '{"type":"progress","status":"working"}' } else { '{"type":"assistant","status":"done"}' }
        $claudeContent = @"
{"type":"user","cwd":"C:\\Work\\MegaProject","message":{"role":"user","content":"Day $day task"}}
{"type":"assistant","message":{"role":"assistant","content":"Day $day processed turn"},"timestamp":"$($fileDate.ToString("o"))"}
$statusLine
"@
        [System.IO.File]::WriteAllText($claudeFile, $claudeContent)
        [System.IO.File]::SetLastWriteTimeUtc($claudeFile, $fileDate.UtcDateTime)

        # Codex file
        $codexSid = "codex-rollout-{0:D4}" -f $day
        $codexFile = Join-Path $codexSessionsDir "$codexSid.jsonl"
        $codexStatus = if ($day -eq 0) { "running" } else { "done" }
        $codexContent = @"
{"type":"session_meta","payload":{"id":"$codexSid","cwd":"C:\\Work\\ApiBackend","source":"cli"}}
{"type":"turn","payload":{"status":"$codexStatus","timestamp":"$($fileDate.ToString("o"))"}}
"@
        [System.IO.File]::WriteAllText($codexFile, $codexContent)
        [System.IO.File]::SetLastWriteTimeUtc($codexFile, $fileDate.UtcDateTime)
    }
    Write-Host "      Generated 730+ files spanning 365 days." -ForegroundColor Green
} else {
    Write-Host "[3/4] Reusing existing 1-year fixture (730+ files) at $fixtureDir" -ForegroundColor Green
}

# 4. Launch AgentIsland with isolated paths, auto-popup, pinned expand, and settings inspector
Write-Host "[4/4] Launching AgentIsland with isolated test paths and real UI..." -ForegroundColor Yellow

$env:CLAUDE_CONFIG_DIR = (Join-Path $fixtureDir "claude-config")
$env:CLAUDE_DESKTOP_DIR = (Join-Path $fixtureDir "claude-desktop")
$env:CODEX_HOME = (Join-Path $fixtureDir "codex-home")
$env:AGENTISLAND_DATA_DIR = $dataDir
$env:AGENTISLAND_DEBUG = "1"
$env:AGENTISLAND_AUTO_POPUP = "1"
$env:AGENTISLAND_PIN_EXPANDED = "1"
$env:AGENTISLAND_DEBUG_OPEN_SETTINGS = "1"

$proc = Start-Process -FilePath $exePath -WorkingDirectory $repoRoot -PassThru
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  AgentIsland UI launched successfully! (PID: $($proc.Id))" -ForegroundColor Green
Write-Host "  1. Dynamic Island: Auto-expanded at the top-center of your screen!" -ForegroundColor Cyan
Write-Host "  2. Settings / Usage Inspector Window: Opened in center screen!" -ForegroundColor Cyan
Write-Host "  3. Tracking: Strictly 2 Agents (Claude + Codex) with 365 days history." -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Green
