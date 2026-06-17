<#
  Spike 3 - USB source detection (read-only; safe to run repeatedly)

  Purpose: validate what the Windows PnpUsbMonitor needs:
    * Enumerate attached USB devices and parse VID/PID  (-> IUsbMonitor.Snapshot)
    * Confirm the KVM hub "source signal" behaves as the prototype found: the SIGNAL PID
      (VID_05E3 PID_0626) appears/disappears as the KVM switches, while the always-on sibling
      (PID_0610) stays present and must NOT be used as the signal.
    * Confirm a device-change event mechanism fires, with a periodic reconcile as the safety-net
      (the KVM re-enumeration storm starves a pure debounce - the prototype's lesson).

  ACTIONS:
    list  (default)  - one-shot: show all instances of the target VID + a present/absent summary
    watch            - watch for ~$Seconds; switch the KVM back and forth and observe transitions

  USAGE (Windows PowerShell 5.1 - powershell.exe):
    powershell -ExecutionPolicy Bypass -File .\Spike3-Usb.ps1
    powershell -ExecutionPolicy Bypass -File .\Spike3-Usb.ps1 -Action watch -Seconds 60

  Keep this file pure ASCII (PS 5.1 reads BOM-less files as Windows-1252).
#>
param([ValidateSet('list', 'watch')][string]$Action = 'list', [int]$Seconds = 60)

$ErrorActionPreference = 'Stop'

# ---- Target source (edit to match your KVM hub) ----
$Vid        = '05E3'           # vendor (Genesys Logic)
$SignalPids = @('0626')        # PID(s) that DISAPPEAR on switch-away = the source signal
$OtherPids  = @('0610')        # always-on sibling instance(s); NOT a valid signal

if ($PSVersionTable.PSVersion.Major -ge 6) {
  Write-Warning "Best run in Windows PowerShell 5.1 (powershell.exe) for CIM event parity."
}

$AllPids = @($SignalPids + $OtherPids)

# Parse present USB devices into [pscustomobject]@{Vid;Pid;Status;Name;InstanceId}
function Get-UsbDevices {
  Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match 'USB\\VID_' } |
    ForEach-Object {
      if ($_.InstanceId -match 'VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})') {
        [pscustomobject]@{
          Vid        = $matches[1].ToUpper()
          DevPid     = $matches[2].ToUpper()
          Status     = $_.Status
          Name       = $_.FriendlyName
          InstanceId = $_.InstanceId
        }
      }
    }
}

# Present PIDs (sorted, unique) for the target VID that are in our watch set.
function Get-TargetPids {
  $present = New-Object System.Collections.Generic.List[string]
  foreach ($d in (Get-UsbDevices)) {
    if ($d.Vid -eq $Vid.ToUpper() -and ($AllPids -contains $d.DevPid)) {
      [void]$present.Add($d.DevPid)
    }
  }
  @($present | Sort-Object -Unique)
}

function Show-Summary {
  $present = Get-TargetPids
  $signalPresent = @($present | Where-Object { $SignalPids -contains $_ }).Count -gt 0
  Write-Host ("  VID {0}: present PIDs = [{1}]" -f $Vid, ($present -join ', '))
  Write-Host ("  signal present (this machine selected): {0}" -f $signalPresent)
}

Write-Host "PowerShell: $($PSVersionTable.PSVersion)   Action: $Action`n"

if ($Action -eq 'list') {
  Write-Host "All present USB instances for VID ${Vid}:"
  $hits = Get-UsbDevices | Where-Object { $_.Vid -eq $Vid.ToUpper() } | Sort-Object DevPid
  if (-not $hits) { Write-Host "  (none - is the KVM selected to this machine?)" }
  foreach ($d in $hits) {
    $tag = if ($SignalPids -contains $d.DevPid) { '[SIGNAL]' } elseif ($OtherPids -contains $d.DevPid) { '[always-on]' } else { '' }
    Write-Host ("  PID {0} {1,-12} Status={2,-12} {3}" -f $d.DevPid, $tag, $d.Status, $d.Name)
    Write-Host ("      {0}" -f $d.InstanceId)
  }
  Write-Host ""
  Show-Summary
  Write-Host "`nTotal present USB devices: $((Get-UsbDevices | Measure-Object).Count)"
  Write-Host "`nRun '-Action watch', then switch the KVM away and back, to see the signal PID toggle."
  return
}

# ---- watch ----
Write-Host "Watching for $Seconds s. Switch the KVM away from this machine, then back.`n"

# Device-change events (arrival/removal). We use them only as wakeups; correctness comes from the
# reconcile below - exactly the PnpUsbMonitor design (event-driven + ~2s reconcile safety-net).
$wokeCount = 0
try {
  Register-CimIndicationEvent -Query "SELECT * FROM Win32_DeviceChangeEvent" `
    -SourceIdentifier 'usbchange' -ErrorAction Stop | Out-Null
  $haveEvents = $true
} catch {
  Write-Host "  (Win32_DeviceChangeEvent subscription unavailable: $_ - reconcile-only)"
  $haveEvents = $false
}

$prev = '<init>'
$end = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $end) {
  $woke = $false
  if ($haveEvents) {
    $evts = @(Get-Event -SourceIdentifier 'usbchange' -ErrorAction SilentlyContinue)
    if ($evts.Count -gt 0) { $evts | Remove-Event; $woke = $true; $wokeCount += $evts.Count }
  }
  $present = Get-TargetPids
  $key = ($present -join ',')
  if ($key -ne $prev) {
    $signalPresent = @($present | Where-Object { $SignalPids -contains $_ }).Count -gt 0
    $note = if ($woke) { '(woke on device-change event)' } else { '(seen via reconcile)' }
    Write-Host ("[{0}] present PIDs=[{1}]  signal={2}  {3}" -f (Get-Date -Format HH:mm:ss), $key, $signalPresent, $note)
    $prev = $key
  }
  Start-Sleep -Milliseconds 1000
}

if ($haveEvents) { Unregister-Event -SourceIdentifier 'usbchange' -ErrorAction SilentlyContinue }
Write-Host "`nDone. Device-change events observed: $wokeCount."
