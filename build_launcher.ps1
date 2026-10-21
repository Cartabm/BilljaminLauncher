# Build a standalone launcher in this source folder without changing the installed launcher.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
Write-Host "Building Billjamin Launcher 1.2.0 (Windows x64)..." -ForegroundColor Cyan
dotnet publish .\TowerOfBilljaminLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o .\publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }
$exe = Join-Path $PSScriptRoot "publish\BilljaminLauncher.exe"
if (-not (Test-Path $exe)) { throw "Expected launcher executable not found: $exe" }
Write-Host "Build succeeded: $exe" -ForegroundColor Green
Write-Host "TEST THIS BUILD BEFORE INSTALLING OR PUBLISHING IT." -ForegroundColor Yellow
