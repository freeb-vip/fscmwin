param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputDir = "artifacts/Fscm.Edge.Win",
    [string]$Version = "0.1.0",
    [string]$GoExe = $env:GO_EXE,
    [string]$DotnetExe = $env:DOTNET_EXE,
    [string]$InnoSetupExe = $env:INNO_SETUP_EXE,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Resolve-InnoSetup([string]$ConfiguredPath) {
    if (-not [string]::IsNullOrWhiteSpace($ConfiguredPath) -and (Test-Path $ConfiguredPath)) {
        return (Resolve-FullPath $ConfiguredPath)
    }
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command -and $command.Source) { return $command.Source }
    foreach ($candidate in @("C:\Program Files (x86)\Inno Setup 6\ISCC.exe", "C:\Program Files\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $candidate) { return $candidate }
    }
    throw "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php or pass -InnoSetupExe C:\Path\To\ISCC.exe."
}

$packageArgs = @{
    Configuration = $Configuration
    RuntimeIdentifier = $RuntimeIdentifier
    OutputDir = $OutputDir
    EdgeVersion = $Version
}
if ($GoExe) { $packageArgs.GoExe = $GoExe }
if ($DotnetExe) { $packageArgs.DotnetExe = $DotnetExe }
if ($NoRestore) { $packageArgs.NoRestore = $true }

& (Join-Path $PSScriptRoot "package-windows-edge.ps1") @packageArgs

$iscc = Resolve-InnoSetup $InnoSetupExe
$sourceDir = Resolve-FullPath $OutputDir
$scriptPath = Resolve-FullPath "installer/Fscm.Edge.Win.iss"
& $iscc "/DMyAppVersion=$Version" "/DSourceDir=$sourceDir" $scriptPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

Write-Host "Installer created in artifacts: FSCM-Edge-Setup-$Version.exe"
