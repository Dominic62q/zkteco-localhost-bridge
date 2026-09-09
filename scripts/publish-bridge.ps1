# Publishes the bridge as one self-contained x86 exe.
# No .NET install needed on the machine that runs it — only the ZKTeco driver.
# Run from the project root.
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $root "dist\bridge"

$env:ZK_SDK_PATH = if ($env:ZK_SDK_PATH) { $env:ZK_SDK_PATH } else {
  "C:\Users\rigel\Downloads\9774a946c3f659ddf2ae90bc8dadc3eb\ZKFingerSDK_Windows_Standard\ZKFinger Standard SDK 5.3.0.33"
}

& dotnet publish (Join-Path $root "bridge\bridge.csproj") `
  -c Release -r win-x86 --self-contained `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  -o $publishDir

Write-Host ""
Write-Host "Published to: $publishDir\bridge.exe"
Write-Host "Run it from the bridge folder (it keeps data\ next to it):"
Write-Host "  cd $root\bridge; ..\dist\bridge\bridge.exe"
