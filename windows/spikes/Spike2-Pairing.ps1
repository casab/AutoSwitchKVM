<#
  Spike 2 - WinRT pairing ceremony  (** THIS CHANGES STATE **)

  Goal: confirm we can PAIR and UNPAIR the Classic-HID Magic Trackpad headlessly via native WinRT,
  with NO PolarGoose CLI. This is the core Milestone 0 question.

  Approach: CUSTOM pairing - Pairing.Custom.PairAsync(ConfirmOnly) with an auto-accept handler.
  The Magic Trackpad needs a custom ConfirmOnly ceremony (basic PairAsync() rejects it instantly:
  CanPair=False, ~35ms Failed). The catch in PowerShell 5.1: a scriptblock handler deadlocks (the
  runspace is single-threaded and our blocking await starves the callback -> RejectedByHandler).
  Fix: the PairingRequested handler is a COMPILED .NET delegate (PairAccepter, added below), so the
  callback runs on its own thread and calls Accept() without needing the blocked runspace. This is
  the exact shape the C# app uses; WinRT itself works (the Windows GUI + UnpairAsync both prove it).

  Actions:
    status   (default) - read-only; current paired/connected state + CanPair
    pair               - PairAsync, then verify ConnectionStatus / HID nodes
    unpair             - UnpairAsync (remove the bond), then verify

  USAGE (Windows PowerShell 5.1 - powershell.exe, NOT pwsh/PS7):
    powershell -ExecutionPolicy Bypass -File .\Spike2-Pairing.ps1
    powershell -ExecutionPolicy Bypass -File .\Spike2-Pairing.ps1 -Action pair
    powershell -ExecutionPolicy Bypass -File .\Spike2-Pairing.ps1 -Action unpair

  KEY CONSTRAINT (the bond is EXCLUSIVE): while one host holds the pairing the other host cannot
  connect. To bring the trackpad TO Windows it must first be FREE of the Mac, and to give it BACK to
  the Mac you must UNPAIR it on Windows (there is no passive handoff and no real "disconnect" for
  Classic HID - removing the bond IS the disconnect).

  TEST RECIPE for the realistic handoff:
    1. Make the trackpad free of the Mac: on the Mac, remove/forget it (or disconnect+power it off)
       so it is not bonded/held by the Mac and goes discoverable. It should currently show
       IsPaired=False on Windows (you already removed it there).
    2. Run  -Action pair  and watch: does PairAsync return Paired, and do ConnectionStatus/HID nodes
       come up within a few seconds? If it returns AuthenticationFailure/Failed, the Mac probably
       still holds it - free it from the Mac first, then retry.
    3. Run  -Action unpair  to remove the bond on Windows, so the Mac can reclaim it. This is exactly
       what you do by hand today in Windows Bluetooth settings - the app will automate it.

  Keep this file pure ASCII (PS 5.1 reads BOM-less files as Windows-1252).
#>
param([ValidateSet('status', 'discover', 'pair', 'unpair')][string]$Action = 'status')

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

# Compiled auto-accept handler: a REAL .NET delegate target (not a PS scriptblock), so the
# PairingRequested callback runs on its own thread and does not need the blocked, single-threaded
# PowerShell runspace. Uses reflection for Accept() so no WinRT SDK / winmd reference is required.
Add-Type -TypeDefinition @"
using System;
public static class PairAccepter {
    public static string LastKind = "(handler never fired)";
    public static void OnPairingRequested(object sender, object e) {
        Type t = e.GetType();
        object kind = t.GetProperty("PairingKind").GetValue(e, null);
        LastKind = kind.ToString();
        if (LastKind == "ProvidePin") {
            t.GetMethod("Accept", new Type[]{ typeof(string) }).Invoke(e, new object[]{ "0000" });
        } else {
            t.GetMethod("Accept", Type.EmptyTypes).Invoke(e, null);
        }
    }
}
"@

# Force-load WinRT types
[void][Windows.Devices.Bluetooth.BluetoothDevice, Windows, ContentType = WindowsRuntime]
[void][Windows.Devices.Enumeration.DeviceInformation, Windows, ContentType = WindowsRuntime]
[void][Windows.Devices.Enumeration.DeviceInformationCollection, Windows, ContentType = WindowsRuntime]
[void][Windows.Devices.Enumeration.DevicePairingResult, Windows, ContentType = WindowsRuntime]
[void][Windows.Devices.Enumeration.DeviceUnpairingResult, Windows, ContentType = WindowsRuntime]
[void][Windows.Devices.Enumeration.DevicePairingKinds, Windows, ContentType = WindowsRuntime]
[void][Windows.Devices.Enumeration.DeviceInformationCustomPairing, Windows, ContentType = WindowsRuntime]
[void][Windows.Devices.Enumeration.DevicePairingRequestedEventArgs, Windows, ContentType = WindowsRuntime]

Write-Host "PowerShell: $($PSVersionTable.PSVersion)   Action: $Action`n"

$macDigits = ($BtMac -replace '[-:]', '').ToUpper()
$addr = [Convert]::ToUInt64($macDigits, 16)

function Get-Target {
  Await ([Windows.Devices.Bluetooth.BluetoothDevice]::FromBluetoothAddressAsync($addr)) ([Windows.Devices.Bluetooth.BluetoothDevice])
}

function Show-State($label) {
  $bd = Get-Target
  if (-not $bd) { Write-Host "  [$label] FromBluetoothAddressAsync returned null"; return }
  $p = $bd.DeviceInformation.Pairing
  Write-Host ("  [$label] name='{0}'  conn={1}  IsPaired={2}  CanPair={3}" -f $bd.Name, $bd.ConnectionStatus, $p.IsPaired, $p.CanPair)
  $hid = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match "BTHENUM\\.*DEV_$macDigits" }
  if ($hid) { foreach ($n in $hid) { Write-Host ("    HID node: {0}  Status={1}" -f $n.InstanceId, $n.Status) } }
  else { Write-Host "    (no BTHENUM HID nodes)" }
}

# Discover the trackpad as a *freshly discovered unpaired* association endpoint.
# You cannot pair the cached FromBluetoothAddress device - PairAsync rejects it instantly (31ms).
# The pairable object comes from enumerating the unpaired selector (what Settings does internally).
function Find-UnpairedTarget {
  Write-Host "`nDiscovering unpaired in-range devices (snapshot)..."
  $sel = [Windows.Devices.Bluetooth.BluetoothDevice]::GetDeviceSelectorFromPairingState($false)
  $found = Await ([Windows.Devices.Enumeration.DeviceInformation]::FindAllAsync($sel)) ([Windows.Devices.Enumeration.DeviceInformationCollection])
  Write-Host ("  found {0} unpaired device(s):" -f $found.Count)
  foreach ($f in $found) { Write-Host ("    name='{0}'  CanPair={1}  Id={2}" -f $f.Name, $f.Pairing.CanPair, $f.Id) }
  # The endpoint Id carries the MAC WITH colons (e.g. ...-3c:50:02:bf:22:45). Strip separators
  # from the Id before matching the colon-less $macDigits.
  $found | Where-Object { (($_.Id -replace '[:\-]', '').ToUpper()).Contains($macDigits) } | Select-Object -First 1
}

Write-Host "Before:"
Show-State 'before'

if ($Action -eq 'status') { Write-Host "`nDone (read-only)."; return }

if ($Action -eq 'discover') {
  $di = Find-UnpairedTarget
  if ($di) { Write-Host "`n  MATCH for $BtMac : $($di.Id)" }
  else { Write-Host "`n  Trackpad NOT found among discoverable unpaired devices (free it from the Mac + pairing mode?)." }
  Write-Host "`nDone (discover)."; return
}

$bd = Get-Target
if (-not $bd) { throw "Target device not found by address - cannot proceed." }
$pairing = $bd.DeviceInformation.Pairing

if ($Action -eq 'pair') {
  if ($pairing.IsPaired) { Write-Host "`nAlready paired - nothing to do."; Show-State 'after'; return }

  $di = Find-UnpairedTarget
  if (-not $di) {
    Write-Host "`n  Trackpad not discoverable as an unpaired endpoint."
    Write-Host "  -> Free it from the Mac (remove/forget or power off) so it goes discoverable, then retry."
    Show-State 'after'; return
  }

  $custom = $di.Pairing.Custom
  $ht = [Windows.Foundation.TypedEventHandler[Windows.Devices.Enumeration.DeviceInformationCustomPairing, Windows.Devices.Enumeration.DevicePairingRequestedEventArgs]]
  $mi = [PairAccepter].GetMethod('OnPairingRequested')
  $handler = [Delegate]::CreateDelegate($ht, $mi)
  $token = $custom.add_PairingRequested($handler)
  $kinds = [Windows.Devices.Enumeration.DevicePairingKinds]::ConfirmOnly

  Write-Host "`nPairing the DISCOVERED endpoint (custom ConfirmOnly, compiled auto-accept handler)..."
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  try {
    $result = Await ($custom.PairAsync($kinds)) ([Windows.Devices.Enumeration.DevicePairingResult])
    $sw.Stop()
    Write-Host ("Pair result: {0}  ProtectionLevelUsed={1}  ({2} ms)  handlerKind={3}" -f $result.Status, $result.ProtectionLevelUsed, $sw.ElapsedMilliseconds, [PairAccepter]::LastKind)
  } catch {
    $sw.Stop()
    Write-Host "PairAsync FAILED after $($sw.ElapsedMilliseconds) ms: $_"
  } finally {
    try { $custom.remove_PairingRequested($token) } catch { Write-Host "  (remove handler warn: $_)" }
  }

  Write-Host "`nWaiting 4s for HID enumeration..."
  Start-Sleep -Seconds 4
}
elseif ($Action -eq 'unpair') {
  if (-not $pairing.IsPaired) { Write-Host "`nNot paired - nothing to unpair."; Show-State 'after'; return }
  Write-Host "`nUnpairing..."
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  try {
    $res = Await ($pairing.UnpairAsync()) ([Windows.Devices.Enumeration.DeviceUnpairingResult])
    $sw.Stop()
    Write-Host ("Unpair result: {0}  ({1} ms)" -f $res.Status, $sw.ElapsedMilliseconds)
  } catch {
    $sw.Stop()
    Write-Host "UnpairAsync FAILED after $($sw.ElapsedMilliseconds) ms: $_"
  }
  Start-Sleep -Seconds 2
}

Write-Host "`nAfter:"
Show-State 'after'
Write-Host "`nDone."
