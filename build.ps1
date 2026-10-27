# Builds PeakModPacker and deploys it (exe + Mono.Cecil) next to PEAK.exe.
param([switch]$SelfTest)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$gameRoot = Resolve-Path (Join-Path $projectDir "..\..")

dotnet build $projectDir -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if ($SelfTest) {
    & (Join-Path $projectDir "bin\Release\PeakModPacker.exe") --selftest | Out-Null
    Get-Content (Join-Path $projectDir "bin\Release\selftest-report.txt")
}

# Prefer the Desktop working copy if it exists, else the game folder.
$desktopDist = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PeakModPacker'
$dist = if (Test-Path $desktopDist) { $desktopDist } else { Join-Path $gameRoot 'PeakModPacker' }
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item (Join-Path $projectDir "bin\Release\PeakModPacker.exe") $dist -Force
Copy-Item (Join-Path $projectDir "bin\Release\Mono.Cecil*.dll") $dist -Force
Write-Host "deployed -> $dist"
