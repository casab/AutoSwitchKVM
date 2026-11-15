using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoSwitchKVM.App.Platform;

/// Connect and disconnect a paired Bluetooth audio device (headphones, speakers) without
/// unpairing it: what the Connect / Disconnect buttons in Windows' Bluetooth settings do.
///
/// Windows' Bluetooth audio drivers (A2DP sink, Hands-Free) expose kernel-streaming audio filters,
/// and each answers two one-shot properties of KSPROPSETID_BtAudio: RECONNECT (0) and
/// DISCONNECT (1). Sending one to every audio filter of the device moves the whole device: on a
/// Soundcore Motion Boom Plus (2026-11-15), disconnecting only the A2DP filter left the link up
/// (Hands-Free held it); with both, the link dropped ~4 s later and the device stayed paired. A
/// reconnect brought it back within 0.5 s.
///
/// Filters of a device: the A2DP node is BTHENUM\{0000110B-...}...&MAC_..., so the address is in
/// its interface path; the Hands-Free filter sits on BTHHFENUM\BTHHFPAUDIO\..., which carries the
/// address only in its DEVPKEY_Bluetooth_DeviceAddress property. The filters exist while the
/// device is paired, connected or not.
internal static class BluetoothAudio
{
    private static readonly Guid KsCategoryAudio = new("6994AD04-93EF-11D0-A3CC-00A0C9223196");
    private static readonly Guid KsPropSetIdBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private const uint IoctlKsProperty = 0x002F0003;   // CTL_CODE(FILE_DEVICE_KS, 0, METHOD_NEITHER, FILE_ANY_ACCESS)
    private const uint KsPropertyTypeGet = 1;
    private const uint OneShotReconnect = 0;
    private const uint OneShotDisconnect = 1;

    /// The device's audio filter paths (empty: not an audio device, or not paired).
    public static List<string> FiltersFor(string digits)
    {
        var result = new List<string>();
        foreach (var path in InterfacePaths(KsCategoryAudio))
        {
            if (path.Contains(digits, StringComparison.OrdinalIgnoreCase)
                && path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(path);
                continue;
            }
            if (!path.Contains("BTH", StringComparison.OrdinalIgnoreCase)) continue;   // USB/HDA audio: skip quickly
            var address = InterfaceDeviceAddress(path);
            if (address is not null && string.Equals(address, digits, StringComparison.OrdinalIgnoreCase))
                result.Add(path);
        }
        return result;
    }

    public static void Disconnect(IReadOnlyList<string> filters) => OneShotAll(filters, OneShotDisconnect);
    public static void Reconnect(IReadOnlyList<string> filters) => OneShotAll(filters, OneShotReconnect);

    /// Send the property to every filter at once (one took 10 s on the first try on hardware) and
    /// throw if none of them took it.
    private static void OneShotAll(IReadOnlyList<string> filters, uint id)
    {
        var errors = new List<string>();
        Parallel.ForEach(filters, path =>
        {
            try { OneShot(path, id); }
            catch (Exception ex) { lock (errors) errors.Add(ex.Message); }
        });
        if (errors.Count == filters.Count && errors.Count > 0)
            throw new Win32Exception($"{(id == OneShotDisconnect ? "disconnect" : "reconnect")} failed: {string.Join("; ", errors)}");
    }

    private static void OneShot(string path, uint id)
    {
        using var handle = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var property = new KsProperty { Set = KsPropSetIdBtAudio, Id = id, Flags = KsPropertyTypeGet };
        if (!DeviceIoControl(handle, IoctlKsProperty, ref property, Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static List<string> InterfacePaths(Guid category)
    {
        for (var attempt = 0; attempt < 3; attempt++)   // the list can grow between the two calls
        {
            if (CM_Get_Device_Interface_List_SizeW(out var length, ref category, null, 0) != 0 || length <= 1) return new();
            var buffer = new char[length];
            var cr = CM_Get_Device_Interface_ListW(ref category, null, buffer, length, 0);
            if (cr == CrBufferSmall) continue;
            if (cr != 0) return new();
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return new();
    }

    /// DEVPKEY_Bluetooth_DeviceAddress of the device behind an interface path (12 hex digits).
    private static string? InterfaceDeviceAddress(string path)
    {
        var instanceId = InterfaceInstanceId(path);
        if (instanceId is null) return null;
        if (CM_Locate_DevNodeW(out var devInst, instanceId, 0) != 0) return null;
        var key = DevpkeyBluetoothDeviceAddress;
        uint size = 0;
        CM_Get_DevNode_PropertyW(devInst, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out var type, buffer, ref size, 0) != 0 || type != DevpropTypeString) return null;
        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static string? InterfaceInstanceId(string path)
    {
        var key = DevpkeyDeviceInstanceId;
        uint size = 0;
        CM_Get_Device_Interface_PropertyW(path, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(path, ref key, out var type, buffer, ref size, 0) != 0 || type != DevpropTypeString) return null;
        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    // ---- Interop ----

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty { public Guid Set; public uint Id; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey { public Guid Fmtid; public uint Pid; public DevPropKey(Guid f, uint p) { Fmtid = f; Pid = p; } }

    private static readonly DevPropKey DevpkeyDeviceInstanceId = new(new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), 256);
    private static readonly DevPropKey DevpkeyBluetoothDeviceAddress = new(new Guid("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A"), 1);
    private const uint DevpropTypeString = 0x12;
    private const int CrBufferSmall = 0x1A;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid category, string? deviceId, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid category, string? deviceId, char[] buffer, uint length, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_PropertyW(string path, ref DevPropKey key, out uint type, byte[]? buffer, ref uint size, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, byte[]? buffer, ref uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref KsProperty input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
