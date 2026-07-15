param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputDir = "artifacts/Fscm.Edge.Win",
    [string]$BackendRepo = $env:FSCM_BACKEND_REPO,
    [string]$BackendRef = $env:FSCM_BACKEND_REF,
    [string]$EdgeVersion = "",
    [string]$BackendVersion = "",
    [string]$LocalBackendRoot = "fscm",
    [string]$GoExe = $env:GO_EXE,
    [string]$DotnetExe = $env:DOTNET_EXE,
    [switch]$NoRestore,
    [switch]$UseLocalBackend
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Resolve-DotnetExe([string]$ConfiguredDotnetExe) {
    if (-not [string]::IsNullOrWhiteSpace($ConfiguredDotnetExe)) {
        $candidate = Resolve-FullPath $ConfiguredDotnetExe
        if (Test-Path $candidate) {
            return $candidate
        }
        throw "Configured .NET executable was not found: $ConfiguredDotnetExe"
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command -and $command.Source) {
        return $command.Source
    }

    $candidates = @(
        "C:\Program Files\dotnet\dotnet.exe",
        "C:\Program Files (x86)\dotnet\dotnet.exe"
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    throw ".NET SDK was not found. Install the .NET SDK and add dotnet.exe to PATH, rerun with -DotnetExe C:\Path\To\dotnet.exe, or set the DOTNET_EXE environment variable."
}

$buildArgs = @{}
if ($UseLocalBackend) {
    $buildArgs.UseLocalBackend = $true
}
if (-not [string]::IsNullOrWhiteSpace($BackendRepo)) {
    $buildArgs.BackendRepo = $BackendRepo
}
if (-not [string]::IsNullOrWhiteSpace($BackendRef)) {
    $buildArgs.BackendRef = $BackendRef
}
if (-not [string]::IsNullOrWhiteSpace($EdgeVersion)) {
    $buildArgs.EdgeVersion = $EdgeVersion
} elseif (-not [string]::IsNullOrWhiteSpace($BackendVersion)) {
    $buildArgs.BackendVersion = $BackendVersion
}
if (-not [string]::IsNullOrWhiteSpace($LocalBackendRoot)) {
    $buildArgs.LocalBackendRoot = $LocalBackendRoot
}
if (-not [string]::IsNullOrWhiteSpace($GoExe)) {
    $buildArgs.GoExe = $GoExe
}

& (Join-Path $PSScriptRoot "build-edge-backend.ps1") @buildArgs

$dotnetCommand = Resolve-DotnetExe $DotnetExe

# EdgeRuntime contains mutable operator data. Preserve it across application
# publishing so an app update never resets connection credentials, templates,
# or the local print-history database.
$runtimePath = Join-Path (Resolve-FullPath $OutputDir) "EdgeRuntime"
$runtimeBackupPath = Join-Path ([System.IO.Path]::GetTempPath()) ("fscm-edge-runtime-" + [Guid]::NewGuid().ToString("N"))
if (Test-Path $runtimePath) {
    New-Item -ItemType Directory -Force -Path $runtimeBackupPath | Out-Null
    foreach ($entry in @("edge.config.yaml", "print-templates.json", "data")) {
        $source = Join-Path $runtimePath $entry
        if (Test-Path $source) {
            Copy-Item -LiteralPath $source -Destination $runtimeBackupPath -Recurse -Force
        }
    }
}

$publishArgs = @(
    "publish",
    "src/Fscm.Edge.Win/Fscm.Edge.Win.csproj",
    "-c", $Configuration,
    "-r", $RuntimeIdentifier,
    "--self-contained", "false",
    "-o", $OutputDir
)
if ($NoRestore) {
    $publishArgs += "--no-restore"
}
if (-not [string]::IsNullOrWhiteSpace($EdgeVersion)) {
    $publishArgs += "-p:Version=$EdgeVersion"
    $publishArgs += "-p:AssemblyVersion=$EdgeVersion"
    $publishArgs += "-p:FileVersion=$EdgeVersion"
}
try {
    & $dotnetCommand @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path $runtimeBackupPath) {
        New-Item -ItemType Directory -Force -Path $runtimePath | Out-Null
        foreach ($entry in @("edge.config.yaml", "print-templates.json", "data")) {
            $source = Join-Path $runtimeBackupPath $entry
            if (Test-Path $source) {
                Copy-Item -LiteralPath $source -Destination $runtimePath -Recurse -Force
            }
        }
        Remove-Item -LiteralPath $runtimeBackupPath -Recurse -Force
    }
}

$launcherOutputDir = Join-Path (Resolve-FullPath $OutputDir) "UpdateLauncher"
$launcherPublishArgs = @(
    "publish",
    "src/Fscm.Edge.UpdateLauncher/Fscm.Edge.UpdateLauncher.csproj",
    "-c", $Configuration,
    "-r", $RuntimeIdentifier,
    "--self-contained", "false",
    "-o", $launcherOutputDir
)
if ($NoRestore) {
    $launcherPublishArgs += "--no-restore"
}
& $dotnetCommand @launcherPublishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish for update launcher failed with exit code $LASTEXITCODE."
}

Write-Host "Packaged FSCM Edge Windows app at $OutputDir"
