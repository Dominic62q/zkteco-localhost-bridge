# Starts bridge + frontend for local development. Run from the project root:
#   .\scripts\start-dev.ps1
# Needs: .NET 8 SDK, Node 22+, ZKTeco driver installed.
# Opens two windows: bridge (http://127.0.0.1:5050) and UI (http://127.0.0.1:5173).
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent

$env:ZK_SDK_PATH = if ($env:ZK_SDK_PATH) { $env:ZK_SDK_PATH } else {
  "C:\Users\rigel\Downloads\9774a946c3f659ddf2ae90bc8dadc3eb\ZKFingerSDK_Windows_Standard\ZKFinger Standard SDK 5.3.0.33"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  Write-Host "dotnet not found. Install the .NET 8 SDK first."
  exit 1
}
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
  Write-Host "npm not found. Install Node 22+ first."
  exit 1
}

Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root\bridge'; dotnet run"
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root\frontend'; if (-not (Test-Path node_modules)) { npm install }; npm run dev -- --host 127.0.0.1"

Write-Host "Bridge:   http://127.0.0.1:5050/api/health"
Write-Host "Frontend: http://127.0.0.1:5173"
