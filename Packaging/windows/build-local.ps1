# Builds the Windows MSI and portable zip locally, mirroring what
# .github/workflows/release.yml does on windows-latest.
# Usage: pwsh Packaging/windows/build-local.ps1 [-Version 1.0.0]
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$publishDir = Join-Path $repoRoot "artifacts\publish\win-x64"
$outDir = Join-Path $repoRoot "artifacts\dist"

# Windows Installer only understands a numeric a.b.c.d. Strip any SemVer pre-release suffix
# and pad to four parts, so -Version 1.0.0, 1.0.0.0 and 1.1.0-rc.1 all produce something valid.
$numeric = ($Version -split '-')[0]
$parts = @($numeric -split '\.') + @('0', '0', '0', '0')
$msiVersion = ($parts[0..3]) -join '.'

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

dotnet publish (Join-Path $repoRoot "WindowAudioRecorder.csproj") -c Release -r win-x64 --self-contained true `
    "-p:Version=$numeric" -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$staging = Join-Path $repoRoot "artifacts\portable\WindowsAudioRecorder"
if (Test-Path (Split-Path $staging)) { Remove-Item (Split-Path $staging) -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item -Path "$publishDir\*" -Destination $staging -Recurse
Compress-Archive -Path $staging -DestinationPath (Join-Path $outDir "WindowsAudioRecorder-$Version-win-x64-portable.zip") -Force

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    dotnet tool install --global wix
}
wix eula accept wix7 | Out-Null
wix extension add WixToolset.UI.wixext WixToolset.Util.wixext | Out-Null

wix build (Join-Path $repoRoot "Packaging\windows\Product.wxs") `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -sw1077 `
    -arch x64 `
    -d "ProductVersion=$msiVersion" `
    -d "PublishDir=$publishDir" `
    -d "RepoRoot=$repoRoot" `
    -out (Join-Path $outDir "WindowsAudioRecorder-$Version-win-x64.msi")

Write-Host "Built $outDir\WindowsAudioRecorder-$Version-win-x64.msi (ProductVersion $msiVersion)"
Write-Host "Built $outDir\WindowsAudioRecorder-$Version-win-x64-portable.zip"
