param(
    [string]$Version = "0.0.1-test"
)

$ErrorActionPreference = "Stop"
$Root = "$PSScriptRoot\.."

$Arch = (Get-WmiObject Win32_Processor).Architecture
if ($Arch -eq 12) {
    # ARM64
    $Rid = "win-arm64"; $Profile = "Windows-ARM64"; $PublishDir = "bin\ARM64\Release\net10.0\win-arm64\publish"
} else {
    # x64 (and everything else falls back to x64)
    $Rid = "win-x64"; $Profile = "Windows-x64"; $PublishDir = "bin\x64\Release\net10.0\win-x64\publish"
}

Write-Host "==> Platform: $Rid  Version: $Version"

Set-Location $Root

Write-Host "==> Restoring tools..."
dotnet tool restore

Write-Host "==> Publishing..."
$env:RELEASE_VERSION = $Version
dotnet publish "/p:PublishProfile=$Profile" -c Release

Write-Host "==> Packing with Velopack..."
$OutputDir = "$Root\releases\$Rid"
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
dotnet vpk pack `
    --packId dir2site `
    --packVersion $Version `
    --packDir "$Root\$PublishDir" `
    --outputDir $OutputDir `
    --runtime $Rid

Write-Host ""
Write-Host "==> Done. Artifacts in releases\$Rid:"
Get-ChildItem $OutputDir | Format-Table Name, Length -AutoSize
