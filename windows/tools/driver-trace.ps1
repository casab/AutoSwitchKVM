<#
.SYNOPSIS
  Capture and decode WPP traces from the Magic Trackpad 2 Bluetooth filter driver (AmtPtpHidFilter),
  so the driver's view of a KVM handoff can be lined up with the AutoSwitch KVM app log.

.DESCRIPTION
  start   (admin)  Start a real-time ETW session for the driver's WPP provider, writing to
                   %LOCALAPPDATA%\AutoSwitchKVM\logs\driver-<timestamp>.etl (circular, 64 MB).
                   The session runs until 'stop' or a reboot.
  stop    (admin)  Stop the session.
  status           Show whether the session is running.
  decode           Decode an .etl (default: the newest driver-*.etl) to a .txt next to it, using
                   TMF files generated from the driver PDB. Timestamps are local time, like the
                   app log. Needs the WDK's tracepdb.exe / tracefmt.exe.

  What the driver traces (all categories are enabled; none fire per touch report): device start/
  stop, the multitouch SET_FEATURE (0xF1) and haptic configuration with retries, transport read
  failures/recovery, battery reads, HID requests.

  The PDB must match the installed driver build. Check with:
    Get-FileHash <package>\AMD64\AmtPtpHidFilter.sys and the copy under
    C:\Windows\System32\DriverStore\FileRepository\amtptpdevice.inf_*\AmtPtpHidFilter.sys

.EXAMPLE
  # Admin PowerShell, before testing:
  .\driver-trace.ps1 start
  # ...do the KVM switches...
  .\driver-trace.ps1 stop
  # Any PowerShell:
  .\driver-trace.ps1 decode
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('start', 'stop', 'status', 'decode')]
    [string]$Action,

    # .etl to decode (default: newest driver-*.etl in the log folder).
    [string]$Etl,

    # Driver PDB (default: the signed package under reference_win_driver at the repo root).
    [string]$Pdb
)

$ErrorActionPreference = 'Stop'

$SessionName = 'AutoSwitchKVM-AmtPtpHidFilter'
$ProviderGuid = '{eeb34803-f3ec-4bb1-97ce-b42405d7f3c2}'   # AmtPtpHidFilter/include/Trace.h
$Flags = '0x1F'    # TRACE_DRIVER | TRACE_DEVICE | TRACE_QUEUE | TRACE_HID | TRACE_INPUT
$Level = '4'       # TRACE_LEVEL_INFORMATION (includes warnings and errors)
$LogDir = Join-Path $env:LOCALAPPDATA 'AutoSwitchKVM\logs'

function Test-Admin {
    $p = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Find-WdkTool([string]$name) {
    $root = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem $root -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tool) { throw "$name not found under $root (install the Windows Driver Kit)." }
    return $tool.FullName
}

function Get-DefaultPdb {
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
    $hit = Get-ChildItem (Join-Path $repoRoot 'reference_win_driver') -Recurse -Filter 'AmtPtpHidFilter.pdb' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'AMD64' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $hit) { throw "AmtPtpHidFilter.pdb not found under reference_win_driver; pass -Pdb." }
    return $hit.FullName
}

switch ($Action) {
    'start' {
        if (-not (Test-Admin)) { throw "'start' needs an elevated (admin) PowerShell." }
        New-Item -ItemType Directory -Force $LogDir | Out-Null
        $out = Join-Path $LogDir ("driver-{0:yyyyMMdd-HHmmss}.etl" -f (Get-Date))
        & logman start $SessionName -ets -p $ProviderGuid $Flags $Level -o $out -mode Circular -max 64 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "logman start failed ($LASTEXITCODE). Already running? Try: .\driver-trace.ps1 stop" }
        Write-Host "Driver tracing started -> $out"
    }
    'stop' {
        if (-not (Test-Admin)) { throw "'stop' needs an elevated (admin) PowerShell." }
        & logman stop $SessionName -ets | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "logman stop failed ($LASTEXITCODE). Is the session running?" }
        Write-Host "Driver tracing stopped. Decode with: .\driver-trace.ps1 decode"
    }
    'status' {
        $info = & logman query $SessionName -ets 2>&1
        if ($LASTEXITCODE -eq 0) { $info | Out-Host }
        else { Write-Host "Session '$SessionName' is not running." }
    }
    'decode' {
        if (-not $Etl) {
            $latest = Get-ChildItem $LogDir -Filter 'driver-*.etl' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $latest) { throw "No driver-*.etl in $LogDir; pass -Etl." }
            $Etl = $latest.FullName
        }
        if (-not $Pdb) { $Pdb = Get-DefaultPdb }

        $tmfDir = Join-Path $LogDir 'driver-tmf'
        New-Item -ItemType Directory -Force $tmfDir | Out-Null
        & (Find-WdkTool 'tracepdb.exe') -f $Pdb -p $tmfDir | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "tracepdb failed ($LASTEXITCODE) for $Pdb" }

        $txt = [IO.Path]::ChangeExtension($Etl, '.txt')
        & (Find-WdkTool 'tracefmt.exe') $Etl -p $tmfDir -o $txt -nosummary | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "tracefmt failed ($LASTEXITCODE) for $Etl" }
        Write-Host "Decoded -> $txt"
    }
}

exit 0
