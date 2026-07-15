param(
    [string]$EdgeBackendRoot = "edge-backend",
    [string]$OutputDir = "src/Fscm.Edge.Win/EdgeRuntime",
    [string]$GoExe = $env:GO_EXE,
    [string]$GoCacheDir = "",
    [string]$EdgeVersion = "",
    [string]$BackendVersion = "",
    [string]$EdgeApiVersion = "edge.proxy.v1",
    [string]$SchemaVersion = "1",
    [string]$BackendRepo = "",
    [string]$BackendRef = "",
    [string]$LocalBackendRoot = "",
    [switch]$UseLocalBackend
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Resolve-GoExe([string]$ConfiguredGoExe) {
    if (-not [string]::IsNullOrWhiteSpace($ConfiguredGoExe)) {
        $candidate = Resolve-FullPath $ConfiguredGoExe
        if (Test-Path $candidate) { return $candidate }
        throw "Configured Go executable was not found: $ConfiguredGoExe"
    }
    $command = Get-Command go -ErrorAction SilentlyContinue
    if ($command -and $command.Source) { return $command.Source }
    foreach ($candidate in @("C:\Program Files\Go\bin\go.exe", "C:\Go\bin\go.exe")) {
        if (Test-Path $candidate) { return $candidate }
    }
    throw "Go SDK was not found. Install Go or provide -GoExe C:\Path\To\go.exe."
}

$edgeRoot = Resolve-FullPath $EdgeBackendRoot
if (-not (Test-Path (Join-Path $edgeRoot "go.mod"))) { throw "Independent edge backend was not found at $edgeRoot" }
$outputRoot = Resolve-FullPath $OutputDir
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$goCommand = Resolve-GoExe $GoExe
$goCache = if ([string]::IsNullOrWhiteSpace($GoCacheDir)) {
    Resolve-FullPath ".gocache-edge"
} else {
    Resolve-FullPath $GoCacheDir
}
New-Item -ItemType Directory -Force -Path $goCache | Out-Null

$commit = "workspace"
try { $commit = (git -C $edgeRoot rev-parse HEAD).Trim() } catch { }
if ([string]::IsNullOrWhiteSpace($EdgeVersion)) {
    $EdgeVersion = $BackendVersion
}
if ([string]::IsNullOrWhiteSpace($EdgeVersion)) {
    $EdgeVersion = if ($commit.Length -ge 12) { $commit.Substring(0, 12) } else { "dev" }
}

$exePath = Join-Path $outputRoot "fscm-edge.exe"
$ldflags = "-s -w -X fscm-edge/internal/version.Version=$EdgeVersion -X fscm-edge/internal/version.Commit=$commit -X fscm-edge/internal/version.APIVersion=$EdgeApiVersion"
$previousGoCache = $env:GOCACHE
Push-Location $edgeRoot
try {
    $env:GOCACHE = $goCache
    & $goCommand build -trimpath -ldflags $ldflags -o $exePath ./cmd/fscm-edge
    if ($LASTEXITCODE -ne 0) { throw "go build failed with exit code $LASTEXITCODE." }
} finally {
    $env:GOCACHE = $previousGoCache
    Pop-Location
}

if (-not (Test-Path $exePath)) { throw "go build completed without creating $exePath." }
$sha256 = (Get-FileHash -Path $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    edge_version = $EdgeVersion
    edge_commit = $commit
    edge_api_version = $EdgeApiVersion
    runtime_kind = "independent_proxy"
    binary = "fscm-edge.exe"
    sha256 = $sha256
    built_at = (Get-Date).ToUniversalTime().ToString("o")
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $outputRoot "edge-runtime-manifest.json") -Encoding UTF8

Write-Host "Built independent FSCM edge proxy:"
Write-Host "  Source: $edgeRoot"
Write-Host "  Output: $exePath"
Write-Host "  SHA256: $sha256"
