[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PlayerPath,

    [string]$PersistentDataPath = (Join-Path $env:USERPROFILE "AppData\LocalLow\iSteelFelix\neon-companion"),

    [string]$EvidenceDirectory = (Join-Path $PWD "companion-windows-evidence"),

    [int]$StartupTimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"

function Get-CompanionChildren {
    param([int]$ParentProcessId)

    return @(Get-CimInstance Win32_Process | Where-Object {
        $_.ParentProcessId -eq $ParentProcessId -and
        $_.CommandLine -match "(^|\s)--companion-player(\s|$)"
    })
}

function Save-ProcessSnapshot {
    param([string]$Name)

    Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -eq $resolvedPlayer -or
        $_.CommandLine -match "(^|\s)--companion-player(\s|$)"
    } |
        Select-Object ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine |
        ConvertTo-Json -Depth 4 |
        Set-Content -Encoding UTF8 (Join-Path $EvidenceDirectory "$Name-processes.json")
}

function Wait-ForCompanionChild {
    param([int]$ParentProcessId)

    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    do {
        $children = Get-CompanionChildren -ParentProcessId $ParentProcessId
        if ($children.Count -eq 1) {
            return $children[0]
        }
        if ($children.Count -gt 1) {
            throw "Expected one Companion child, found $($children.Count)."
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    Save-ProcessSnapshot -Name "timeout-process-spawn"
    throw "PROCESS_SPAWN_TIMEOUT: Companion child did not start within $StartupTimeoutSeconds seconds."
}

function Wait-ForLogText {
    param(
        [string]$Path,
        [string]$Pattern,
        [string]$Reason
    )

    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    do {
        if ((Test-Path $Path) -and (Select-String -Path $Path -Pattern $Pattern -Quiet)) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    Save-ProcessSnapshot -Name "timeout-$($Reason.ToLowerInvariant())"
    throw "${Reason}_TIMEOUT: Log pattern '$Pattern' was not observed in $Path."
}

function Get-FileHashOrNull {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return $null
    }
    return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash
}

function Read-PreferenceSnapshot {
    param([string]$Path)

    $settings = Get-Content -Raw -Path $Path | ConvertFrom-Json
    return [ordered]@{
        companionModeEnabled = [bool]$settings.companionModeEnabled
        companionWindowVisible = [bool]$settings.companionWindowVisible
        companionWindowPinned = [bool]$settings.companionWindowPinned
        companionWindowClickThrough = [bool]$settings.companionWindowClickThrough
        companionWindowMonitor = [int]$settings.companionWindowMonitor
        companionWindowScale = [double]$settings.companionWindowScale
        companionWindowPositionX = [int]$settings.companionWindowPositionX
        companionWindowPositionY = [int]$settings.companionWindowPositionY
    }
}

function Close-OwnedParent {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process -or $Process.HasExited) {
        return
    }
    if (-not $Process.CloseMainWindow()) {
        throw "The launched parent did not expose a closeable main window."
    }
    if (-not $Process.WaitForExit(10000)) {
        throw "The launched parent did not exit after CloseMainWindow()."
    }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "This acceptance harness requires Windows PowerShell/PowerShell on Windows."
}

$resolvedPlayer = (Resolve-Path $PlayerPath).Path
$settingsPath = Join-Path $PersistentDataPath "appsettings.json"
$sessionPath = Join-Path $PersistentDataPath "sessions.json"
$providerPath = Join-Path $PersistentDataPath "providers.json"
$secretPath = Join-Path $PersistentDataPath "secrets.json"
$childLog = Join-Path $PersistentDataPath "Logs\companion-player.log"

if (-not (Test-Path $settingsPath)) {
    throw "Missing $settingsPath. Launch once, enable Companion mode, set controls, and close the app."
}

$preferencesBefore = Read-PreferenceSnapshot -Path $settingsPath
if (-not $preferencesBefore.companionModeEnabled) {
    throw "Companion mode is disabled. Enable it in the Windows Player, set the requested controls, close, then rerun."
}

New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
Copy-Item $settingsPath (Join-Path $EvidenceDirectory "appsettings-before.json") -Force
$dataHashesBefore = [ordered]@{
    sessions = Get-FileHashOrNull $sessionPath
    providers = Get-FileHashOrNull $providerPath
    secrets = Get-FileHashOrNull $secretPath
}

$firstParent = $null
$secondParent = $null
$firstChild = $null
$secondChild = $null
try {
    $firstLog = Join-Path $EvidenceDirectory "parent-first.log"
    $firstParent = Start-Process -FilePath $resolvedPlayer -ArgumentList @("-logFile", "`"$firstLog`"") -PassThru
    $firstChild = Wait-ForCompanionChild -ParentProcessId $firstParent.Id
    Wait-ForLogText -Path $firstLog -Pattern "\[CompanionWindow\] IPC connected:" -Reason "PIPE_CONNECTION"
    Wait-ForLogText -Path $firstLog -Pattern "\[CompanionWindow\] Runtime ready\." -Reason "RUNTIME_READY"
    $firstChildren = Get-CompanionChildren -ParentProcessId $firstParent.Id
    if ($firstChildren.Count -ne 1) {
        Save-ProcessSnapshot -Name "exactly-one-child-failed"
        throw "CHILD_COUNT_INVALID: Expected exactly one ready Companion child, found $($firstChildren.Count)."
    }

    $childLogText = ""
    if (Test-Path $childLog) {
        $childLogText = Get-Content -Raw $childLog
    }
    if ($childLogText -match "App bootstrap completed|\[Bootstrap\]|ProviderConfigRepository|ChatSessionRepository") {
        throw "Child log contains parent-only bootstrap/provider/session initialization."
    }

    @($firstParent.Id, [int]$firstChild.ProcessId) |
        ForEach-Object { Get-CimInstance Win32_Process -Filter "ProcessId=$_" } |
        Select-Object ProcessId, ParentProcessId, ExecutablePath, CommandLine |
        ConvertTo-Json -Depth 4 |
        Set-Content -Encoding UTF8 (Join-Path $EvidenceDirectory "processes-first.json")

    Stop-Process -Id $firstChild.ProcessId -Force
    Start-Sleep -Seconds 2
    $firstParent.Refresh()
    if ($firstParent.HasExited) {
        throw "Parent exited when the Companion child was force-closed."
    }
    Close-OwnedParent -Process $firstParent

    $secondLog = Join-Path $EvidenceDirectory "parent-second.log"
    $secondParent = Start-Process -FilePath $resolvedPlayer -ArgumentList @("-logFile", "`"$secondLog`"") -PassThru
    $secondChild = Wait-ForCompanionChild -ParentProcessId $secondParent.Id
    Wait-ForLogText -Path $secondLog -Pattern "\[CompanionWindow\] IPC connected:" -Reason "PIPE_CONNECTION"
    Wait-ForLogText -Path $secondLog -Pattern "\[CompanionWindow\] Runtime ready\." -Reason "RUNTIME_READY"
    $secondChildren = Get-CompanionChildren -ParentProcessId $secondParent.Id
    if ($secondChildren.Count -ne 1) {
        Save-ProcessSnapshot -Name "exactly-one-child-restart-failed"
        throw "CHILD_COUNT_INVALID: Expected exactly one ready Companion child after restart, found $($secondChildren.Count)."
    }

    $preferencesAfter = Read-PreferenceSnapshot -Path $settingsPath
    if (($preferencesBefore | ConvertTo-Json -Compress) -ne
        ($preferencesAfter | ConvertTo-Json -Compress)) {
        throw "Companion preferences changed across the automated restart."
    }

    Close-OwnedParent -Process $secondParent
    $deadline = (Get-Date).AddSeconds(10)
    do {
        $remainingChild = Get-Process -Id $secondChild.ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $remainingChild) {
            break
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    if ($null -ne (Get-Process -Id $secondChild.ProcessId -ErrorAction SilentlyContinue)) {
        throw "Companion child remained alive after parent shutdown."
    }

    $dataHashesAfter = [ordered]@{
        sessions = Get-FileHashOrNull $sessionPath
        providers = Get-FileHashOrNull $providerPath
        secrets = Get-FileHashOrNull $secretPath
    }
    if (($dataHashesBefore | ConvertTo-Json -Compress) -ne
        ($dataHashesAfter | ConvertTo-Json -Compress)) {
        throw "Provider/session/secret persistence changed during display-only lifecycle automation."
    }

    Copy-Item $settingsPath (Join-Path $EvidenceDirectory "appsettings-after.json") -Force
    if (Test-Path $childLog) {
        Copy-Item $childLog (Join-Path $EvidenceDirectory "companion-player.log") -Force
    }
    [ordered]@{
        passed = $true
        player = $resolvedPlayer
        firstParentPid = $firstParent.Id
        firstChildPid = [int]$firstChild.ProcessId
        secondParentPid = $secondParent.Id
        secondChildPid = [int]$secondChild.ProcessId
        preferences = $preferencesAfter
        protectedDataHashes = $dataHashesAfter
        manualEvidenceStillRequired = @(
            "transparent screenshot and monitor/scale placement",
            "all four avatar backends and motion-state parity",
            "TTS lipsync plus immediate stop/cancel/barge-in reset",
            "drag, pin, show/hide, Settings, Column, click-through and Ctrl+Shift+F12",
            "send a chat message after child close and confirm same session/history"
        )
    } | ConvertTo-Json -Depth 6 |
        Set-Content -Encoding UTF8 (Join-Path $EvidenceDirectory "result.json")

    Write-Host "Companion Windows lifecycle/persistence automation passed. Evidence: $EvidenceDirectory"
}
finally {
    foreach ($process in @($firstParent, $secondParent)) {
        if ($null -ne $process) {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
            $process.Dispose()
        }
    }
    foreach ($child in @($firstChild, $secondChild)) {
        if ($null -ne $child) {
            Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}
