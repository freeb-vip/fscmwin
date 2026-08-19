param(
    [Parameter(Mandatory = $true)]
    [string]$ServiceExecutable,
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,
    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

$ErrorActionPreference = "Stop"

function Invoke-ServiceControl([string]$ArgumentLine) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "$env:SystemRoot\System32\sc.exe"
    $startInfo.Arguments = $ArgumentLine
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $standardOutput = $process.StandardOutput.ReadToEnd()
    $standardError = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($exitCode -ne 0) {
        $detail = @($standardOutput, $standardError) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        throw ("sc.exe failed with exit code {0}: {1}" -f
            $exitCode,
            ($detail -join [Environment]::NewLine))
    }
}

function Protect-InstallDirectory([string]$Path) {
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetOwner([System.Security.Principal.SecurityIdentifier]::new("S-1-5-32-544"))
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) {
        [void]$acl.RemoveAccessRuleAll($rule)
    }

    $inheritance = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    $rules = @(
        [System.Security.AccessControl.FileSystemAccessRule]::new(
            [System.Security.Principal.SecurityIdentifier]::new("S-1-5-18"),
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            $propagation,
            $allow),
        [System.Security.AccessControl.FileSystemAccessRule]::new(
            [System.Security.Principal.SecurityIdentifier]::new("S-1-5-32-544"),
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            $propagation,
            $allow),
        [System.Security.AccessControl.FileSystemAccessRule]::new(
            [System.Security.Principal.SecurityIdentifier]::new("S-1-5-32-545"),
            [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
            $inheritance,
            $propagation,
            $allow)
    )
    foreach ($rule in $rules) {
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Get-ConfiguredPort([string]$Path) {
    $port = 8089
    $inServerSection = $false
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^server\s*:\s*$') {
            $inServerSection = $true
            continue
        }
        if ($inServerSection -and $line -match '^\S') {
            break
        }
        if ($inServerSection -and $line -match '^\s+port\s*:\s*["'']?([0-9]{1,5})["'']?\s*$') {
            $candidate = [int]$Matches[1]
            if ($candidate -lt 1 -or $candidate -gt 65535) {
                throw ("Invalid server port in " + $Path + ": " + $candidate)
            }
            $port = $candidate
            break
        }
    }
    return $port
}

function Get-ConfiguredDatabasePath([string]$Path) {
    $databasePath = "data\edge.db"
    $inDatabaseSection = $false
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^database\s*:\s*$') {
            $inDatabaseSection = $true
            continue
        }
        if ($inDatabaseSection -and $line -match '^\S') {
            break
        }
        if ($inDatabaseSection -and $line -match '^\s+sqlite_path\s*:\s*(.+?)\s*$') {
            $candidate = $Matches[1].Trim().Trim('"').Trim("'")
            if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                $databasePath = $candidate
            }
            break
        }
    }
    if (-not [System.IO.Path]::IsPathRooted($databasePath)) {
        $databasePath = Join-Path (Split-Path -Parent $Path) $databasePath
    }
    return [System.IO.Path]::GetFullPath($databasePath)
}

function Test-DatabaseCorruption([string]$ErrorLogPath) {
    if (-not (Test-Path -LiteralPath $ErrorLogPath -PathType Leaf)) {
        return $false
    }
    return [bool](Select-String -LiteralPath $ErrorLogPath -Pattern 'database disk image is malformed' -Quiet)
}

function Backup-CorruptDatabase([string]$DatabasePath) {
    $backupPath = $DatabasePath + ".corrupt-" + (Get-Date -Format "yyyyMMdd-HHmmss")
    $moved = $false
    foreach ($suffix in @("", "-wal", "-shm")) {
        $sourcePath = $DatabasePath + $suffix
        if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
            Move-Item -LiteralPath $sourcePath -Destination ($backupPath + $suffix) -Force
            $moved = $true
        }
    }
    if (-not $moved) {
        throw "SQLite corruption was reported, but no database files were found at $DatabasePath."
    }
    return $backupPath
}

function Wait-ServiceHealth(
    [int]$Port,
    [string]$ErrorLogPath,
    [string]$StartFailure = "") {
    $healthUrl = "http://127.0.0.1:$Port/edge/health"
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        try {
            $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) {
                return
            }
        } catch {
        }
        if (Test-DatabaseCorruption -ErrorLogPath $ErrorLogPath) {
            break
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    $diagnostics = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($StartFailure)) {
        [void]$diagnostics.Add("Start-Service error: $StartFailure")
    }

    $service = Get-Service -Name "FscmEdge" -ErrorAction SilentlyContinue
    $serviceStatus = if ($null -eq $service) { "Not installed" } else { $service.Status.ToString() }
    [void]$diagnostics.Add("Service status: $serviceStatus")

    try {
        $serviceQuery = (& "$env:SystemRoot\System32\sc.exe" queryex FscmEdge 2>&1 | Out-String).Trim()
        if (-not [string]::IsNullOrWhiteSpace($serviceQuery)) {
            [void]$diagnostics.Add("sc.exe queryex FscmEdge:$([Environment]::NewLine)$serviceQuery")
        }
    } catch {
        [void]$diagnostics.Add("Unable to query service details: $($_.Exception.Message)")
    }

    try {
        $portPattern = ":" + $Port + "\s"
        $portOwners = @(& "$env:SystemRoot\System32\netstat.exe" -ano -p tcp 2>&1 |
            Where-Object { $_ -match $portPattern })
        if ($portOwners.Count -gt 0) {
            [void]$diagnostics.Add("TCP port $Port usage:$([Environment]::NewLine)$($portOwners -join [Environment]::NewLine)")
        } else {
            [void]$diagnostics.Add("TCP port $Port is not listening.")
        }
    } catch {
        [void]$diagnostics.Add("Unable to inspect TCP port " + $Port + ": " + $_.Exception.Message)
    }

    $backendError = ""
    if (Test-Path -LiteralPath $ErrorLogPath -PathType Leaf) {
        $backendError = (Get-Content -LiteralPath $ErrorLogPath -Tail 30) -join [Environment]::NewLine
        if (-not [string]::IsNullOrWhiteSpace($backendError)) {
            [void]$diagnostics.Add("Backend error log ($ErrorLogPath):$([Environment]::NewLine)$backendError")
        }
    }
    if ([string]::IsNullOrWhiteSpace($backendError)) {
        [void]$diagnostics.Add("No backend error was written. Check Windows Event Viewer and endpoint protection history.")
    }

    throw "FSCM Edge did not become healthy at $healthUrl.$([Environment]::NewLine)$($diagnostics -join ([Environment]::NewLine + [Environment]::NewLine))"
}

try {
    $ServiceExecutable = [System.IO.Path]::GetFullPath($ServiceExecutable)
    $ConfigPath = [System.IO.Path]::GetFullPath($ConfigPath)
    $LogPath = [System.IO.Path]::GetFullPath($LogPath)
    Remove-Item -LiteralPath $LogPath -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $ServiceExecutable -PathType Leaf)) {
        throw "Service executable was not installed: $ServiceExecutable"
    }
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        throw "Service configuration was not installed: $ConfigPath"
    }

    $installDirectory = Split-Path -Parent (Split-Path -Parent $ServiceExecutable)
    $installRoot = [System.IO.Path]::GetPathRoot($installDirectory)
    if ([string]::IsNullOrWhiteSpace($installRoot) -or $installDirectory.StartsWith("\\")) {
        throw "The service must be installed on a local drive: $installDirectory"
    }
    if ($installDirectory.TrimEnd("\") -eq $installRoot.TrimEnd("\")) {
        throw "The service cannot be installed directly in a drive root: $installDirectory"
    }
    Protect-InstallDirectory $installDirectory

    $binaryPath = '"' + $ServiceExecutable + '" --mode=edge --config="' + $ConfigPath + '"'
    $escapedBinaryPath = $binaryPath.Replace('"', '\"')
    $service = Get-Service -Name "FscmEdge" -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        $serviceArguments = 'create FscmEdge binPath= "' + $escapedBinaryPath +
            '" start= delayed-auto error= normal DisplayName= "FSCM Edge Backend"'
    } else {
        $serviceArguments = 'config FscmEdge binPath= "' + $escapedBinaryPath +
            '" start= delayed-auto error= normal DisplayName= "FSCM Edge Backend"'
    }

    Invoke-ServiceControl -ArgumentLine $serviceArguments
    Invoke-ServiceControl -ArgumentLine (
        'description FscmEdge "Provides the FSCM edge proxy, SMB NAS storage, and retention cleanup."')
    Invoke-ServiceControl -ArgumentLine (
        'failure FscmEdge reset= 86400 actions= restart/5000/restart/15000/restart/60000')
    Invoke-ServiceControl -ArgumentLine 'failureflag FscmEdge 1'
    try {
        if (-not [System.Diagnostics.EventLog]::SourceExists("FscmEdge")) {
            New-EventLog -LogName Application -Source "FscmEdge"
        }
    } catch {
        "Unable to register FscmEdge event source: $($_.Exception.Message)" |
            Out-File -LiteralPath $LogPath -Append -Encoding utf8
    }

    $configuredPort = Get-ConfiguredPort -Path $ConfigPath
    $databasePath = Get-ConfiguredDatabasePath -Path $ConfigPath
    $errorLogDirectory = Split-Path -Parent $LogPath
    $errorLogPath = Join-Path $errorLogDirectory "edge.stderr.log"
    $previousErrorLogPath = Join-Path $errorLogDirectory "edge.stderr.previous.log"
    if (Test-Path -LiteralPath $errorLogPath -PathType Leaf) {
        Move-Item -LiteralPath $errorLogPath -Destination $previousErrorLogPath -Force
    }

    $startFailure = ""
    try {
        Start-Service -Name "FscmEdge"
    } catch {
        $startFailure = $_.Exception.Message
    }

    try {
        Wait-ServiceHealth -Port $configuredPort -ErrorLogPath $errorLogPath -StartFailure $startFailure
    } catch {
        if (-not (Test-DatabaseCorruption -ErrorLogPath $errorLogPath)) {
            throw
        }

        Stop-Service -Name "FscmEdge" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
        $databaseBackupPath = Backup-CorruptDatabase -DatabasePath $databasePath
        $corruptionLogPath = Join-Path $errorLogDirectory "edge.stderr.corrupt-db.log"
        if (Test-Path -LiteralPath $errorLogPath -PathType Leaf) {
            Move-Item -LiteralPath $errorLogPath -Destination $corruptionLogPath -Force
        }
        "Recovered malformed SQLite database. Backup: $databaseBackupPath" |
            Out-File -LiteralPath $LogPath -Append -Encoding utf8

        $retryFailure = ""
        try {
            Start-Service -Name "FscmEdge"
        } catch {
            $retryFailure = $_.Exception.Message
        }
        Wait-ServiceHealth -Port $configuredPort -ErrorLogPath $errorLogPath -StartFailure $retryFailure
    }
} catch {
    $logDirectory = Split-Path -Parent $LogPath
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    $_ | Out-File -LiteralPath $LogPath -Force -Encoding utf8
    throw
}