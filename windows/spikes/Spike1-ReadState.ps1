<#
  Spike 1 - Bluetooth read-state probe (no pairing changes; safe to run repeatedly)

  Purpose: confirm the WinRT plumbing works and that we can read everything the Windows
  IBluetoothController needs on the *read* side:
    * Bluetooth adapter power      (-> isPoweredOn)
    * Paired devices + addresses   (-> pairedDevices, MAC handling)
    * Target device ConnectionStatus + Classic-HID PnP nodes  (-> isConnected)

  HOW TO RUN: Windows PowerShell 5.1 (run `powershell.exe`, NOT `pwsh`/PowerShell 7).
  5.1 has the built-in WinRT type projection this uses; PS7 resolves these types differently.

  EDIT the MAC below to your trackpad, then run. Paste me the full output.
#>

$ErrorActionPreference = 'Stop'
$BtMac = '3C:50:02:BF:22:45'   # <-- set your Magic Trackpad MAC

if ($PSVersionTable.PSVersion.Major -ge 6) {
  Write-Warning "Run this in Windows PowerShell 5.1 (powershell.exe), not PowerShell 7 - WinRT type access differs."
}

# --- WinRT async/await helper (PS 5.1) ---
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
  $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
  $_.GetParameters()[0].ParameterType.IsGenericType -and
  $_.GetParameters()[0].ParameterType.GetGenericTypeDefinition().Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
function Await($op, [type]$T) {
  $task = $asTask.MakeGenericMethod($T).Invoke($null, @($op))
  [void]$task.Wait(-1)
  $task.Result
}

# Force-load the WinRT types we use
[void][Windows.Devices.Radios.Radio, Windows, ContentType = WindowsRuntime]
[void][Windows.Devices.Bluetooth.BluetoothDevice, Windows, ContentType = WindowsRuntime]
[void][Windows.Devices.Enumeration.DeviceInformation, Windows, ContentType = WindowsRuntime]

Write-Host "PowerShell: $($PSVersionTable.PSVersion)`n"

# --- 1) Bluetooth radio (adapter) power ---
try {
  $radios = Await ([Windows.Devices.Radios.Radio]::GetRadiosAsync()) ([System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]])
  $bt = $radios | Where-Object { $_.Kind -eq [Windows.Devices.Radios.RadioKind]::Bluetooth } | Select-Object -First 1
  Write-Host ("Bluetooth radio state: {0}" -f $(if ($bt) { $bt.State } else { '<no BT radio found>' }))
} catch {
  Write-Host "Radio read FAILED: $_"
}

# --- 2) Paired classic Bluetooth devices ---
Write-Host "`nPaired classic Bluetooth devices:"
try {
  $selector = [Windows.Devices.Bluetooth.BluetoothDevice]::GetDeviceSelectorFromPairingState($true)
  $devs = Await ([Windows.Devices.Enumeration.DeviceInformation]::FindAllAsync($selector)) ([Windows.Devices.Enumeration.DeviceInformationCollection])
  if (-not $devs -or $devs.Count -eq 0) { Write-Host "  (none)" }
  foreach ($d in $devs) {
    $bd = Await ([Windows.Devices.Bluetooth.BluetoothDevice]::FromIdAsync($d.Id)) ([Windows.Devices.Bluetooth.BluetoothDevice])
    $mac = '?'
    $conn = '?'
    if ($bd) {
      $hex = '{0:X12}' -f $bd.BluetoothAddress
      $mac = (0..5 | ForEach-Object { $hex.Substring($_ * 2, 2) }) -join ':'
      $conn = $bd.ConnectionStatus
    }
    Write-Host ("  {0,-30} {1}  conn={2}  paired={3}" -f $d.Name, $mac, $conn, $d.Pairing.IsPaired)
  }
} catch {
  Write-Host "  enumeration FAILED: $_"
}

# --- 3) Target device by MAC: ConnectionStatus + Classic-HID PnP nodes ---
$macDigits = ($BtMac -replace '[-:]', '').ToUpper()
Write-Host "`nTarget $BtMac (digits $macDigits):"

# ConnectionStatus via FromBluetoothAddressAsync (classic)
try {
  $addr = [Convert]::ToUInt64($macDigits, 16)
  $bd = Await ([Windows.Devices.Bluetooth.BluetoothDevice]::FromBluetoothAddressAsync($addr)) ([Windows.Devices.Bluetooth.BluetoothDevice])
  if ($bd) {
    Write-Host ("  FromBluetoothAddress: name='{0}'  conn={1}  paired={2}" -f $bd.Name, $bd.ConnectionStatus, $bd.DeviceInformation.Pairing.IsPaired)
  } else {
    Write-Host "  FromBluetoothAddressAsync returned null (device unknown / not in range / unpaired)"
  }
} catch {
  Write-Host "  FromBluetoothAddress FAILED: $_"
}

# Classic-HID PnP nodes (the prototype's connection signal)
$hid = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match "BTHENUM\\.*DEV_$macDigits" }
if ($hid) {
  foreach ($n in $hid) { Write-Host ("  HID node: {0}  Status={1}" -f $n.InstanceId, $n.Status) }
} else {
  Write-Host "  no BTHENUM HID nodes for this MAC (device not connected/paired as Classic HID)"
}

Write-Host "`nDone. Try once with the trackpad ACTIVE on Windows, and once switched AWAY, so we can see how conn/HID-nodes change."
