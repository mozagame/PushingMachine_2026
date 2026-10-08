<#
  Builds the production package on Windows 10/11 (VS 2022 / .NET 8 SDK).
  1. runs the test suite (must be 0 failed)
  2. publishes CheckBarcode.exe self-contained win-x64
  3. publishes the PLC simulator (FAT / training)
  4. writes SHA-256 manifest and zips the package
  Usage:  powershell -ExecutionPolicy Bypass -File build\publish.ps1 [-Version 2.0.0]
#>
param([string]$Version = "2.0.0", [string]$Out = "artifacts")
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$pkg = Join-Path $Out "CheckBarcode-$Version-win-x64"
Remove-Item $pkg -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $pkg | Out-Null

Write-Host "== tests"
dotnet run --project tests\CheckBarcode.Tests -c Release -- --trace "$Out\trace.md"
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

Write-Host "== publish application"
dotnet publish src\CheckBarcode.Wpf -c Release -r win-x64 --self-contained true `
  -p:Version=$Version -p:DebugType=none -o "$pkg\app"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "== publish PLC simulator"
dotnet publish tools\CheckBarcode.PlcSimulator -c Release -r win-x64 --self-contained true -p:DebugType=none -o "$pkg\PlcSimulator"
if ($LASTEXITCODE -ne 0) { throw "simulator publish failed" }

Copy-Item docs -Destination "$pkg\docs" -Recurse
Copy-Item "$Out\trace.md" "$pkg\docs\TEST_TRACE.md"

Write-Host "== manifest"
$manifest = Get-ChildItem $pkg -Recurse -File | ForEach-Object {
  "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.FullName.Substring($pkg.Length + 1)
}
$manifest | Set-Content "$pkg\SHA256SUMS.txt" -Encoding UTF8
Compress-Archive -Path "$pkg\*" -DestinationPath "$pkg.zip" -Force
Write-Host "Package: $pkg.zip"
