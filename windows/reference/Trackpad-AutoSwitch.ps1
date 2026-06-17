<# =======================================================================
 KVM-driven Bluetooth handoff for Apple Magic Trackpad 2 (Windows 11, PS7)
 -----------------------------------------------------------------------
 • No polling: CIM extrinsic events + Kernel-PnP EventLog watcher
 • Selection: look for KVM hubs VID_05E3&PID_{0610,0626}; learn your pair
 • Actions:
     Selected   → Pair (PolarGoose CLI) → verify HID nodes
     Unselected → Unpair (PolarGoose CLI) → verify cleanup
 • Transport detection: Classic vs BLE (Classic expected for Magic Trackpad 2)
 • Optional BLE path via WinRT is kept (best-effort) but not required.

 Prereq (pair helper):
   winget install -e PolarGoose.BluetoothDevicePairing
   # Provides BluetoothDevicePairing.exe

 Optional autostart (run once, admin):
   $act = New-ScheduledTaskAction -Execute "pwsh.exe" `
          -Argument '-NoProfile -WindowStyle Hidden -File "C:\Tools\Trackpad-AutoSwitch.ps1"'
   $trg = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
   Register-ScheduledTask -TaskName "Trackpad-AutoSwitch" -Action $act -Trigger $trg -RunLevel Highest -Force
======================================================================= #>

[CmdletBinding()]
param(
  [string]$LogPath = "$env:LOCALAPPDATA\KvmBleHandoff\kvm-bt-handoff.log"
)

$script:VerboseOn = ($PSBoundParameters.ContainsKey('Verbose') -or $VerbosePreference -ne 'SilentlyContinue')

# -----------------------
# Config (edit if needed)
# -----------------------
$script:BtMac      = '3C:50:02:BF:22:45'                     # Trackpad MAC
$script:MacDigits  = ($script:BtMac -replace '[-:]', '').ToUpper()
$script:DebounceMs = 1200
$script:ConnectRetryMax        = 6                            # BLE only (~30s total)
$script:ConnectRetryIntervalMs = 5000

# KVM hubs (Genesys Logic)
function Is-KvmHubId([string]$Id) { return $Id -imatch '^USB\\VID_05E3&PID_(0626|0610)\\' }

# Device ID patterns
$script:DevIdPatternLE      = '^BTHLE(Device)?\\DEV_' + $script:MacDigits
$script:DevIdPatternClassic = '^BTHENUM\\DEV_'        + $script:MacDigits

# Classic HID (0x1124) service GUID (child nodes under HID class)
$script:HidServiceGuid = '00001124-0000-1000-8000-00805F9B34FB'
$script:HidGuidRe      = '\{' + $script:HidServiceGuid + '\}'

# Learned hub allowlist / group key
$script:KvmAllowIds = [System.Collections.Generic.HashSet[string]]::new()
$script:KvmGroupKey = $null   # e.g. "6&10D2C9A7"

# -----------------------
# Logging
# -----------------------
function Initialize-Log {
  $dir = [System.IO.Path]::GetDirectoryName($LogPath)
  if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
  "[$(Get-Date -Format o)] KVM-BT handoff starting (PS7, CIM+Kernel-PnP, PolarGoose pairing). Verbose=$script:VerboseOn" |
    Out-File -FilePath $LogPath -Encoding utf8 -Append
}
function Write-Log { param([string]$Message, [string]$Level="INFO")
  $line = "[{0}] {1}: {2}" -f (Get-Date -Format "HH:mm:ss.fff"), $Level, $Message
  Write-Host $line
  try {
    if ((Test-Path $LogPath) -and ((Get-Item $LogPath).Length -gt 1MB)) {
      $bak = "$LogPath.1"; if (Test-Path $bak) { Remove-Item $bak -Force -ErrorAction SilentlyContinue }
      Move-Item $LogPath $bak -Force
    }
    $line | Out-File -FilePath $LogPath -Encoding utf8 -Append
  } catch {}
}
function VLog { param([string]$Message) if ($script:VerboseOn) { Write-Log $Message "DEBUG"; Write-Verbose $Message } }

Initialize-Log

# -----------------------
# Modules
# -----------------------
Import-Module PnpDevice -ErrorAction SilentlyContinue | Out-Null
if (-not (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue)) {
  Import-Module PnpDevice -UseWindowsPowerShell -ErrorAction SilentlyContinue | Out-Null
}

# -----------------------
# PnP helpers (hubs + HID)
# -----------------------
function Get-KvmHubDevices {
  Get-PnpDevice -InstanceId 'USB\VID_05E3&*' -ErrorAction SilentlyContinue |
    Where-Object { Is-KvmHubId $_.InstanceId }
}

function Get-InstanceGroupKey([string]$InstanceId) {
  # USB\VID_05E3&PID_0626\6&10D2C9A7&0&4  ->  "6&10D2C9A7"
  $tail = ($InstanceId -split '\\',3)[2]; $parts = $tail -split '&'
  if ($parts.Length -ge 2) { return ($parts[0] + '&' + $parts[1]) }; return $tail
}

function Learn-KvmGroupFromCurrentOk {
  $devsOk = Get-KvmHubDevices | Where-Object Status -eq 'OK'
  if (-not $devsOk) { VLog "Learn: no Status=OK hubs available."; return }
  $rows = foreach ($d in $devsOk) {
    $usbPid = if ($d.InstanceId -match 'PID_(\w{4})') { $matches[1].ToUpper() } else { '' }
    [pscustomobject]@{ Id=$d.InstanceId; UsbPid=$usbPid; Key=(Get-InstanceGroupKey $d.InstanceId); Status=$d.Status; Name=$d.FriendlyName }
  }
  $groups = $rows | Group-Object Key
  $chosen = $null
  foreach ($g in $groups) {
    $pids = $g.Group | ForEach-Object UsbPid | Sort-Object -Unique
    if ($pids -contains '0610' -and $pids -contains '0626') { $chosen = $g; break }
  }
  if (-not $chosen) { $chosen = $groups | Sort-Object Count -Descending | Select-Object -First 1 }
  if ($chosen) {
    $script:KvmAllowIds.Clear(); foreach ($r in $chosen.Group) { [void]$script:KvmAllowIds.Add($r.Id) }
    $script:KvmGroupKey = $chosen.Name
    Write-Log ("Learned KVM hub group key '{0}'. Allowlist = {1}" -f $script:KvmGroupKey, ($script:KvmAllowIds -join ', '))
    if ($script:VerboseOn) { foreach ($r in $chosen.Group) { VLog ("Learn: {0}  Status={1}  PID={2}  Key={3}" -f $r.Id, $r.Status, $r.UsbPid, $r.Key) } }
  } else { VLog "Learn: no suitable group found." }
}

function Test-KvmSelected {
  $all = Get-KvmHubDevices
  if ($script:VerboseOn) { foreach ($d in $all) { VLog ("Probe(all): Status={0}; Name={1}; Id={2}" -f $d.Status, $d.FriendlyName, $d.InstanceId) } }

  # Apply allowlist if learned (still scopes us to the right physical KVM group)
  if ($script:KvmAllowIds.Count -gt 0) {
    $filtered = $all | Where-Object { $script:KvmAllowIds.Contains($_.InstanceId) }
    if ($filtered) { $all = $filtered }
    else { VLog "Allowlist present but none enumerated; falling back to any 05E3 hubs." }
  }

  # Selection signal = the SuperSpeed (PID_0626) hub ONLY.
  # The PID_0610 group contains a permanent/always-on instance that stays
  # Status=OK even when the KVM points at the other host, so it cannot be
  # used to detect switch-away. PID_0626 reliably goes Unknown on deselect.
  $signal = $all | Where-Object { $_.InstanceId -imatch 'PID_0626' }
  if (-not $signal) {
    VLog "No PID_0626 hub found; falling back to any allowlisted hub for presence."
    $signal = $all
  }

  $present = ($signal | Where-Object Status -eq 'OK' | Measure-Object).Count -gt 0
  if ($script:VerboseOn) {
    foreach ($d in $signal) { VLog ("  Signal: Status={0}; Id={1}" -f $d.Status, $d.InstanceId) }
    VLog ("Selected? {0}  (signal=PID_0626; {1} instance(s))" -f $present, @($signal).Count)
  }
  return $present
}

function Get-Transport {
  $nodes = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match $script:DevIdPatternLE -or $_.InstanceId -match $script:DevIdPatternClassic }
  if ($nodes | Where-Object InstanceId -match '^BTHLE')   { return 'LE' }
  if ($nodes | Where-Object InstanceId -match '^BTHENUM') { return 'Classic' }
  return 'Unknown'
}

function Get-TrackpadClassicHidNodes {
  Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
    $_.InstanceId -imatch "^BTHENUM\\$($script:HidGuidRe)" -and $_.InstanceId -imatch $script:MacDigits
  } | Sort-Object InstanceId -Unique
}

# -----------------------
# Pair/unpair via PolarGoose CLI (recommended, no WinRT needed)
# -----------------------
function Get-BtPairExe {
  foreach ($p in @(
    "$env:ProgramFiles\BluetoothDevicePairing\BluetoothDevicePairing.exe",
    "$env:ProgramFiles(x86)\BluetoothDevicePairing\BluetoothDevicePairing.exe",
    "BluetoothDevicePairing.exe"
  )) { try { $cmd = Get-Command $p -ErrorAction Stop; if ($cmd) { return $cmd.Source } } catch {} }
  return $null
}
function Invoke-PolarGoose([string]$Command, [string]$Type, [string]$Pin) {
  $exe = Get-BtPairExe
  if (-not $exe) {
    Write-Log "Pair helper not found (BluetoothDevicePairing.exe). Install: winget install -e PolarGoose.BluetoothDevicePairing" "WARN"
    return @{ Exit=999; Out="NoHelper" }
  }
  $cmdArgs = @($Command, '--mac', $script:BtMac, '--type', $Type)
  if ($Pin) { $cmdArgs += @('--pin', $Pin) }
  $psi = New-Object System.Diagnostics.ProcessStartInfo -Property @{
    FileName = $exe; Arguments = ($cmdArgs -join ' '); RedirectStandardOutput = $true; RedirectStandardError = $true; UseShellExecute=$false; CreateNoWindow=$true
  }
  $p = [System.Diagnostics.Process]::Start($psi)
  $stdout = $p.StandardOutput.ReadToEnd()
  $stderr = $p.StandardError.ReadToEnd()
  $p.WaitForExit()
  if ($p.ExitCode -ne 0 -and $stderr) { Write-Log ("PolarGoose error: {0}" -f $stderr.Trim()) "WARN" }
  return @{ Exit=$p.ExitCode; Out=($stdout + $stderr).Trim() }
}
function Get-BtTypePrimary() {
  switch ($script:Transport) {
    'LE'      { 'BluetoothLE' }
    'Classic' { 'Bluetooth' }
    default   { 'Bluetooth' }  # Magic Trackpad 2 is usually Classic HID
  }
}
function Get-BtTypeAlternate($primary) { if ($primary -eq 'Bluetooth') { 'BluetoothLE' } else { 'Bluetooth' } }

function Test-DevicePaired {
  # If any Classic/BLE PnP nodes for this MAC exist, treat as "already paired"
  $nodes = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
    $_.InstanceId -match $script:DevIdPatternLE -or $_.InstanceId -match $script:DevIdPatternClassic
  }
  if ($nodes -and @($nodes).Count -gt 0) {
    VLog ("Paired check: found {0} PnP node(s) for {1}" -f @($nodes).Count, $script:BtMac)
    return $true
  }
  VLog ("Paired check: no PnP nodes present for {0}" -f $script:BtMac)
  return $false
}

function Pair-Trackpad {
  if (Test-DevicePaired) {
    $msg = "AlreadyPaired(local)"
    Write-Log ("Pair result: {0}" -f $msg)
    return $msg
  }
  # Magic Trackpad 2 is Classic HID; avoid BLE fallback noise
  $res = Invoke-PolarGoose 'pair-by-mac' 'Bluetooth' $null
  Write-Log ("Pair result: {0}" -f ($res.Out -replace '\s+',' '))
  return $res.Out
}
function Unpair-Trackpad {
  # MT2 holds one link key per bond. Once the Mac re-pairs, the key Windows
  # still has is stale, so we must REMOVE the bond on leave (not just
  # disconnect) and re-pair on return. Verify removal; don't fire-and-forget.
  $types = @('Bluetooth','BluetoothLE')   # Classic first, LE as a safety net
  for ($attempt = 1; $attempt -le 3; $attempt++) {
    foreach ($t in $types) {
      $res = Invoke-PolarGoose 'unpair-by-mac' $t $null
      Write-Log ("Unpair attempt {0} type {1}: exit={2}; out={3}" -f `
                 $attempt, $t, $res.Exit, ($res.Out -replace '\s+',' '))
    }
    Start-Sleep -Milliseconds 600
    if (-not (Test-DevicePaired)) {
      Write-Log "Unpair: bond removed; no PnP nodes remain."
      return $true
    }
    Write-Log ("Unpair: nodes still present after attempt {0}; retrying." -f $attempt) "WARN"
  }

  # Last resort: force-remove lingering PnP nodes (needs elevation).
  Write-Log "Unpair: PolarGoose did not clear the bond; falling back to pnputil." "WARN"
  $nodes = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
    $_.InstanceId -match $script:DevIdPatternLE -or $_.InstanceId -match $script:DevIdPatternClassic
  }
  foreach ($n in $nodes) {
    try {
      $out = & pnputil.exe /remove-device $n.InstanceId 2>&1
      Write-Log ("pnputil /remove-device {0} -> {1}" -f $n.InstanceId, ($out -join ' '))
    } catch {
      Write-Log ("pnputil remove failed for {0}: {1}" -f $n.InstanceId, $_) "WARN"
    }
  }
  Start-Sleep -Milliseconds 600
  $still = Test-DevicePaired
  if ($still) { Write-Log "Unpair: device STILL present after pnputil fallback." "WARN" }
  else        { Write-Log "Unpair: device removed via pnputil fallback." }
  return (-not $still)
}


# -----------------------
# BLE connect helpers (best-effort; rarely used for this device)
# -----------------------
$script:WinRtBluetoothAvailable = $false; $script:BleDevice = $null
function Try-EnableWinRtBluetooth {
  try { Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null
        $type = [System.Type]::GetType('Windows.Devices.Bluetooth.BluetoothLEDevice, Windows, ContentType=WindowsRuntime', $false)
        $script:WinRtBluetoothAvailable = [bool]$type; VLog ("WinRT Bluetooth projection available: {0}" -f $script:WinRtBluetoothAvailable)
  } catch { $script:WinRtBluetoothAvailable = $false; VLog ("WinRT Bluetooth projection available: False") }
}
Try-EnableWinRtBluetooth
if ($script:WinRtBluetoothAvailable) { Write-Log "WinRT Bluetooth available for BLE path." } else { Write-Log "WinRT Bluetooth unavailable; BLE connect will not be possible." "WARN" }

function Get-BluetoothAddressCandidates {
  param([Parameter(Mandatory)][string]$Mac)
  $raw = ($Mac -replace '[-:]', '').ToUpper()
  $bytes   = [regex]::Matches($raw, '..') | ForEach-Object { $_.Value }
  $bytesRv = @($bytes); [Array]::Reverse($bytesRv)
  $rev     = ($bytesRv -join '')
  return @([UInt64]("0x$raw"), [UInt64]("0x$rev")) | Select-Object -Unique
}
$script:BtAddrCandidates = Get-BluetoothAddressCandidates -Mac $script:BtMac
function Wait-Async { param($AsyncOp) $task = [System.WindowsRuntimeSystemExtensions]::AsTask($AsyncOp); $task.GetAwaiter().GetResult() }
function Get-BleDeviceHandle { if (-not $script:WinRtBluetoothAvailable) { return $null }
  foreach ($addr in $script:BtAddrCandidates) {
    try { VLog ("BLE FromBluetoothAddressAsync(0x{0})" -f $addr.ToString("X"))
      $dev = Wait-Async ([Windows.Devices.Bluetooth.BluetoothLEDevice]::FromBluetoothAddressAsync($addr))
      if ($null -ne $dev) { return $dev } } catch { VLog ("BLE FromBluetoothAddressAsync exception: {0}" -f $_) }
  } return $null
}
function Connect-TrackpadBLE {
  if (-not $script:WinRtBluetoothAvailable) {
    Write-Log "BLE connect: WinRT unavailable." "WARN"; return $false
  }
  if ($script:BleDevice -and $script:BleDevice.ConnectionStatus -eq [Windows.Devices.Bluetooth.BluetoothConnectionStatus]::Connected) { Write-Log "BLE connect: already connected."; return $true }
  if (-not $script:BleDevice) { $script:BleDevice = Get-BleDeviceHandle; if (-not $script:BleDevice) { Write-Log "BLE connect: FromBluetoothAddressAsync failed" "WARN"; return $false } }
  try {
    VLog "BLE GetGattServicesAsync()"
    $res = Wait-Async ($script:BleDevice.GetGattServicesAsync())
    if ($null -ne $res -and $res.Status -eq [Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus]::Success) {
      Start-Sleep -Milliseconds 200
      if ($script:BleDevice.ConnectionStatus -eq [Windows.Devices.Bluetooth.BluetoothConnectionStatus]::Connected) { Write-Log ("BLE connect: success → {0}" -f ($script:BleDevice.Name)); return $true }
    } else { Write-Log ("BLE connect: GATT status {0}" -f $res.Status) }
  } catch { Write-Log "BLE connect exception: $_" "WARN" }
  return $false
}
function Disconnect-TrackpadBLE {
  try { if ($script:BleDevice) { Write-Log "BLE disconnect: disposing device handle."; $script:BleDevice.Dispose(); $script:BleDevice = $null } }
  catch { Write-Log "BLE disconnect exception: $_" "WARN" }
}

# -----------------------
# Classic connect/disconnect
# -----------------------
function Connect-TrackpadClassic {
  # Pairing already performed by Evaluate-KvmState; wait for stack to enumerate nodes.
  Start-Sleep -Milliseconds 800
  $hid = Get-TrackpadClassicHidNodes
  if ($hid) {
    Write-Log ("Classic connect: {0} HID service node(s) present for {1}." -f $hid.Count, $script:BtMac)
  } else {
    Write-Log "Classic connect: no HID service nodes found yet; BT stack may still be enumerating." "WARN"
  }
  return ($null -ne $hid -and $hid.Count -gt 0)
}
function Disconnect-TrackpadClassic {
  if (Unpair-Trackpad) { Write-Log "Classic disconnect: trackpad fully unpaired and removed." }
  else { Write-Log "Classic disconnect: trackpad NOT fully removed; see log above." "WARN" }
}

# -----------------------
# Debounce / state
# -----------------------
$script:IsSelected = $false
$script:Connected      = $false
$script:PairAttempts   = 0
$script:PairAttemptMax = 8        # ~8 reconciles ≈ 16s, covers the Mac-release race
$script:Transport  = Get-Transport
$script:LastReason = "startup"
$script:UnselectStreak    = 0
$script:UnselectConfirmN  = 2   # reconcile must see unselected this many times in a row
Write-Log "Detected device transport: $script:Transport"
if ($script:Transport -eq 'Unknown') { Write-Log "Hint: pair once and verify: Get-PnpDevice | ? InstanceId -match 'BTH(LEDevice|ENUM)\\DEV_$($script:MacDigits)'" "WARN" }

# Timers
$debounceTimer = New-Object System.Timers.Timer($script:DebounceMs); $debounceTimer.AutoReset = $false
Register-ObjectEvent -InputObject $debounceTimer -EventName Elapsed -SourceIdentifier 'TIMER_Debounce' | Out-Null
$connectTimer = New-Object System.Timers.Timer($script:ConnectRetryIntervalMs); $connectTimer.AutoReset = $true
Register-ObjectEvent -InputObject $connectTimer -EventName Elapsed -SourceIdentifier 'TIMER_Connect' | Out-Null
# Safety-net: periodic reconcile. The 500ms debounce gets starved during the
# USB re-enumeration storm on a KVM switch (events arrive <500ms apart for
# several seconds, perpetually re-arming it), so it can miss the deselect
# entirely. This AutoReset timer is independent of the event flood and
# guarantees state converges within ~2s. Evaluate-KvmState is idempotent —
# it only acts on a transition, so calling it on a schedule is safe & cheap.
$reconcileTimer = New-Object System.Timers.Timer(2000); $reconcileTimer.AutoReset = $true
Register-ObjectEvent -InputObject $reconcileTimer -EventName Elapsed -SourceIdentifier 'TIMER_Reconcile' | Out-Null
$reconcileTimer.Start()
Write-Log "Reconcile timer started (2s safety-net evaluation)."
$script:ConnectAttempts = 0
function Start-ConnectRetries {
  # BLE only
  $script:ConnectAttempts = 0
  [void]$connectTimer.Stop(); [void]$connectTimer.Start()
  $script:ConnectAttempts++; Write-Log ("BLE connect retry {0}/{1}…" -f $script:ConnectAttempts, $script:ConnectRetryMax)
  if (-not (Connect-TrackpadBLE)) {
    if ($script:ConnectAttempts -ge $script:ConnectRetryMax) {
      Write-Log "BLE connect: exhausted attempts; waiting for next event." "WARN"
      [void]$connectTimer.Stop()
    }
  } else {
    [void]$connectTimer.Stop()
  }
}
function Stop-ConnectRetries { [void]$connectTimer.Stop() }

# -----------------------
# Event sources (no polling)
# -----------------------
$null = Register-CimIndicationEvent -Namespace 'root\cimv2' -ClassName Win32_DeviceChangeEvent              -SourceIdentifier 'CIM_DeviceChange'
$null = Register-CimIndicationEvent -Namespace 'root\cimv2' -ClassName Win32_SystemConfigurationChangeEvent -SourceIdentifier 'CIM_SysCfgChange'
Write-Log "CIM subscriptions ready (DeviceChange + SystemConfigurationChange). Watching KVM hubs VID_05E3&PID_{0626,0610}."

# Kernel-PnP System log filter: only our hub instance IDs
$xpath = "*[System[Provider[@Name='Microsoft-Windows-Kernel-PnP']]] and *[EventData[Data[@Name='DeviceInstanceId'] and (contains(., 'USB\VID_05E3&PID_0626') or contains(., 'USB\VID_05E3&PID_0610'))]]"
$elogQuery = New-Object System.Diagnostics.Eventing.Reader.EventLogQuery('System', [System.Diagnostics.Eventing.Reader.PathType]::LogName, $xpath)
$elogWatch = New-Object System.Diagnostics.Eventing.Reader.EventLogWatcher($elogQuery)
Register-ObjectEvent -InputObject $elogWatch -EventName EventRecordWritten -SourceIdentifier 'EVT_KernelPnP' | Out-Null
$elogWatch.Enabled = $true
Write-Log "Kernel-PnP EventLog watcher started."

# -----------------------
# State evaluation
# -----------------------
function Ensure-TrackpadConnected([string]$Reason) {
  # Runs while this host is selected. Pairs (with bounded retries) then connects.
  # On a Mac->Windows handoff the trackpad can still be held by the Mac for a
  # few seconds, so PolarGoose pair returns AuthenticationFailure. We retry on
  # successive reconciles rather than giving up after a single shot.
  if ($script:Connected) { return }

  if (-not (Test-DevicePaired)) {
    if ($script:PairAttempts -ge $script:PairAttemptMax) {
      VLog ("Pair: budget exhausted ({0}); waiting for next deselect/reselect." -f $script:PairAttemptMax)
      return
    }
    $script:PairAttempts++
    Write-Log ("Pair attempt {0}/{1} ({2})…" -f $script:PairAttempts, $script:PairAttemptMax, $Reason)
    $out = Pair-Trackpad
    if ($out -match 'AuthenticationFailure|Failed to pair') {
      Write-Log ("Pair not complete yet (likely Mac still releasing); will retry: {0}" -f ($out -replace '\s+',' ')) "WARN"
      return    # next reconcile (~2s) tries again
    }
    Start-Sleep -Milliseconds 600
  }

  # Paired (or already was) — establish/verify the connection.
  $script:Transport = Get-Transport
  if ($script:Transport -eq 'LE') {
    if (Connect-TrackpadBLE) { $script:Connected = $true } else { Start-ConnectRetries }
  } else {
    if (Connect-TrackpadClassic) {
      $script:Connected = $true
      Write-Log ("Trackpad paired and connected — {0}; transport: {1}" -f $Reason, $script:Transport)
    } else {
      VLog "Classic connect: HID nodes not enumerated yet; will retry next reconcile."
    }
  }
}
function Evaluate-KvmState([string]$Reason = "event") {
  VLog ("Evaluate-KvmState reason = {0}" -f $Reason)
  $present = Test-KvmSelected

  if ($present) {
    $script:UnselectStreak = 0
    if (-not $script:IsSelected) {
      $script:IsSelected   = $true
      $script:Connected    = $false
      $script:PairAttempts = 0
      Learn-KvmGroupFromCurrentOk
      Write-Log ("Selected (hub Status=OK) — {0}" -f $Reason)
    }
    # Initial selection OR a later reconcile: keep driving toward paired+connected.
    Ensure-TrackpadConnected $Reason
  }
elseif ($script:IsSelected) {
    # A deselect from the debounced event path is already coalesced and trusted —
    # act immediately. A deselect seen by the periodic reconcile might be a
    # transient from a fast double-tap, so require it to persist across two
    # consecutive reconcile ticks before tearing down.
    $fromReconcile = ($Reason -eq 'periodic reconcile')
    if ($fromReconcile) {
      $script:UnselectStreak++
      if ($script:UnselectStreak -lt $script:UnselectConfirmN) {
        VLog ("Deselect seen by reconcile ({0}/{1}); deferring teardown in case of fast switch." -f `
              $script:UnselectStreak, $script:UnselectConfirmN)
        return
      }
    }
    $script:IsSelected    = $false
    $script:Connected     = $false
    $script:PairAttempts  = 0
    $script:UnselectStreak = 0
    Write-Log "Unselected (no allowlisted hub Status=OK) — $Reason"
    Stop-ConnectRetries
    $script:Transport = Get-Transport
    if     ($script:Transport -eq 'LE') { Disconnect-TrackpadBLE; Unpair-Trackpad | Out-Null }
    else   { Disconnect-TrackpadClassic }
  }
  else {
    VLog ("No selection change. IsSelected={0}; Connected={1}; present={2}" -f `
          $script:IsSelected, $script:Connected, $present)
  }
}

# Seed initial state
Evaluate-KvmState "startup scan"

# -----------------------
# Main loop
# -----------------------
while ($true) {
  $evt = Wait-Event
  if (-not $evt) { continue }
  switch ($evt.SourceIdentifier) {
    'CIM_DeviceChange'  { $et=$null; try{$et=[int]$evt.SourceEventArgs.NewEvent.EventType}catch{}; VLog ("CIM_DeviceChange fired; EventType={0}" -f ($et ?? '<null>')); [void]$debounceTimer.Stop(); [void]$debounceTimer.Start() }
    'CIM_SysCfgChange'  { $et=$null; try{$et=[int]$evt.SourceEventArgs.NewEvent.EventType}catch{$et=1}; VLog ("CIM_SysCfgChange fired; EventType={0}" -f ($et ?? '<null>')); [void]$debounceTimer.Stop(); [void]$debounceTimer.Start() }
    'EVT_KernelPnP'     { try{$rec=$evt.SourceEventArgs.EventRecord;$xml=[xml]$rec.ToXml();$id=[int]$xml.Event.System.EventID;$dev=($xml.Event.EventData.Data|?{$_.Name -eq 'DeviceInstanceId'}|select -First 1).'#text'; VLog ("Kernel-PnP fired; Id={0}; Device={1}" -f $id,$dev)}catch{VLog ("Kernel-PnP parse fail: {0}" -f $_)}; [void]$debounceTimer.Stop(); [void]$debounceTimer.Start() }
    'TIMER_Debounce'    { Evaluate-KvmState ("debounced change: events") }
    'TIMER_Connect'     { if (-not $script:IsSelected) { Stop-ConnectRetries; break }; $script:ConnectAttempts++; Write-Log ("BLE connect retry {0}/{1}…" -f $script:ConnectAttempts, $script:ConnectRetryMax); if (Connect-TrackpadBLE) { Stop-ConnectRetries } elseif ($script:ConnectAttempts -ge $script:ConnectRetryMax) { Write-Log "BLE connect: exhausted attempts; waiting for next event." "WARN"; Stop-ConnectRetries } }
    'TIMER_Reconcile'   { Evaluate-KvmState "periodic reconcile" }
    default             { VLog ("Unhandled event: {0}" -f $evt.SourceIdentifier) }
  }
  Remove-Event -EventIdentifier $evt.EventIdentifier -ErrorAction SilentlyContinue | Out-Null
}
