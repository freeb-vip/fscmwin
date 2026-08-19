// This Source Code Form is subject to the terms of the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win.Services;

internal sealed partial class RawInputScannerService : IDisposable
{
    private const int WmInput = 0x00FF;
    private const int WmInputDeviceChange = 0x00FE;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RimTypeKeyboard = 1;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const ushort VkShift = 0x10;
    private const ushort VkLeftShift = 0xA0;
    private const ushort VkRightShift = 0xA1;

    private readonly HwndSource _source;
    private readonly Dictionary<nint, RawDevice> _devices = [];
    private readonly Dictionary<nint, ScannerFrameAssembler> _assemblers = [];
    private readonly HashSet<nint> _shiftedDevices = [];
    private ScannerConfiguration _configuration;
    private bool _disposed;

    public RawInputScannerService(Window window, ScannerConfiguration configuration)
    {
        _configuration = ScannerConfigurationService.Normalize(configuration);
        nint handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("Unable to acquire the WPF window source.");
        _source.AddHook(WndProc);
        Register(handle);
        RefreshDevices();
    }

    public event EventHandler<ScannerCapturedFrame>? FrameCaptured;

    public event EventHandler? DevicesChanged;

    public void ApplyConfiguration(ScannerConfiguration configuration)
    {
        _configuration = ScannerConfigurationService.Normalize(configuration);
    }

    public IReadOnlyList<ScannerDeviceConfiguration> DiscoveredDevices()
    {
        return _devices.Values
            .Select(device => new ScannerDeviceConfiguration
            {
                Fingerprint = device.Fingerprint,
                DisplayName = device.Name,
                Transport = "hid",
                SystemPath = device.Path,
                VendorId = device.VendorId,
                ProductId = device.ProductId,
                IdentityConfidence = device.IdentityConfidence,
                ConnectionState = "online",
            })
            .OrderBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void RefreshDevices()
    {
        uint count = 0;
        uint listSize = (uint)Marshal.SizeOf<RawInputDeviceList>();
        if (GetRawInputDeviceList(null, ref count, listSize) == uint.MaxValue || count == 0)
        {
            _devices.Clear();
            DevicesChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        RawInputDeviceList[] list = new RawInputDeviceList[count];
        uint returned = GetRawInputDeviceList(list, ref count, listSize);
        if (returned == uint.MaxValue)
        {
            return;
        }

        Dictionary<nint, RawDevice> devices = [];
        foreach (RawInputDeviceList item in list)
        {
            if (item.Type != RimTypeKeyboard)
            {
                continue;
            }

            string path = ReadDeviceName(item.Device);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            Match match = VidPidRegex().Match(path);
            string vendor = match.Success ? match.Groups[1].Value.ToLowerInvariant() : string.Empty;
            string product = match.Success ? match.Groups[2].Value.ToLowerInvariant() : string.Empty;
            string suffix = string.IsNullOrWhiteSpace(vendor) ? "Unknown USB identity" : $"VID {vendor} / PID {product}";
            bool stable = TryGetUsbParentIdentity(path, out string identity);
            devices[item.Device] = new RawDevice(item.Device, path, Fingerprint(stable ? identity : path), vendor, product, $"HID scanner candidate ({suffix})", stable ? "stable" : "port_fallback");
        }

        _devices.Clear();
        foreach ((nint key, RawDevice value) in devices)
        {
            _devices[key] = value;
        }

        foreach (nint stale in _assemblers.Keys.Except(devices.Keys).ToList())
        {
            _assemblers.Remove(stale);
            _shiftedDevices.Remove(stale);
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(WndProc);
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmInputDeviceChange)
        {
            RefreshDevices();
            return nint.Zero;
        }

        if (message != WmInput)
        {
            return nint.Zero;
        }

        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        _ = GetRawInputData(lParam, RidInput, nint.Zero, ref size, headerSize);
        if (size < headerSize)
        {
            return nint.Zero;
        }

        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RidInput, buffer, ref size, headerSize) != size)
            {
                return nint.Zero;
            }

            RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != RimTypeKeyboard || !_devices.TryGetValue(header.Device, out RawDevice? device))
            {
                return nint.Zero;
            }

            ScannerDeviceConfiguration? configured = (_configuration.Devices ?? []).FirstOrDefault(value => ScannerDevicePolicy.IsActive(value) && value.Transport == "hid" && value.Fingerprint.Equals(device.Fingerprint, StringComparison.OrdinalIgnoreCase));
            if (!_configuration.Enabled || configured is null)
            {
                return nint.Zero;
            }

            RawKeyboard keyboard = Marshal.PtrToStructure<RawKeyboard>(nint.Add(buffer, Marshal.SizeOf<RawInputHeader>()));
            bool keyDown = keyboard.Message is WmKeyDown or WmSysKeyDown;
            if (keyboard.VirtualKey is VkShift or VkLeftShift or VkRightShift)
            {
                if (keyDown)
                {
                    _shiftedDevices.Add(header.Device);
                }
                else
                {
                    _shiftedDevices.Remove(header.Device);
                }

                return nint.Zero;
            }

            if (!keyDown || !TryMapKey(keyboard.VirtualKey, _shiftedDevices.Contains(header.Device), out char value))
            {
                return nint.Zero;
            }

            if (!_assemblers.TryGetValue(header.Device, out ScannerFrameAssembler? assembler))
            {
                assembler = new ScannerFrameAssembler();
                _assemblers[header.Device] = assembler;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string? frame = assembler.Push(value, now, TimeSpan.FromMilliseconds(_configuration.ScanTimeoutMilliseconds), _configuration.MaxScanLength);
            if (frame is not null)
            {
                FrameCaptured?.Invoke(this, new ScannerCapturedFrame(Guid.NewGuid().ToString(), device.Fingerprint, frame, now));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return nint.Zero;
    }

    private static void Register(nint handle)
    {
        RawInputDevice[] devices =
        [
            new() { UsagePage = 0x01, Usage = 0x06, Flags = RidevInputSink | RidevDevNotify, Target = handle },
        ];
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to register keyboard Raw Input.");
        }
    }

    private static string ReadDeviceName(nint device)
    {
        uint size = 0;
        _ = GetRawInputDeviceInfo(device, RidiDeviceName, null, ref size);
        if (size == 0)
        {
            return string.Empty;
        }

        StringBuilder value = new((int)size);
        return GetRawInputDeviceInfo(device, RidiDeviceName, value, ref size) == uint.MaxValue ? string.Empty : value.ToString();
    }

    internal static string Fingerprint(string identity)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.Trim().ToLowerInvariant()))).ToLowerInvariant();
    }

    internal static bool TryMapKey(ushort virtualKey, bool shifted, out char value)
    {
        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            value = (char)(shifted ? virtualKey : virtualKey + 32);
            return true;
        }

        if (virtualKey is >= 0x30 and <= 0x39)
        {
            const string shiftedDigits = ")!@#$%^&*(";
            value = shifted ? shiftedDigits[virtualKey - 0x30] : (char)virtualKey;
            return true;
        }

        if (virtualKey is >= 0x60 and <= 0x69)
        {
            value = (char)('0' + virtualKey - 0x60);
            return true;
        }

        (value, bool found) = virtualKey switch
        {
            0x0D => ('\r', true), 0x08 => ('\b', true), 0x20 => (' ', true),
            0x6A => ('*', true), 0x6B => ('+', true), 0x6D => ('-', true), 0x6E => ('.', true), 0x6F => ('/', true),
            0xBA => (shifted ? ':' : ';', true), 0xBB => (shifted ? '+' : '=', true),
            0xBC => (shifted ? '<' : ',', true), 0xBD => (shifted ? '_' : '-', true),
            0xBE => (shifted ? '>' : '.', true), 0xBF => (shifted ? '?' : '/', true),
            0xC0 => (shifted ? '~' : '`', true), 0xDB => (shifted ? '{' : '[', true),
            0xDC => (shifted ? '|' : '\\', true), 0xDD => (shifted ? '}' : ']', true),
            0xDE => (shifted ? '"' : '\'', true), _ => ('\0', false),
        };
        return found;
    }

    [GeneratedRegex("VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidRegex();

    private static bool TryGetUsbParentIdentity(string rawPath, out string identity)
    {
        identity = string.Empty;
        string instance = rawPath.TrimStart('\\', '?').Replace('#', '\\');
        int interfaceGuid = instance.IndexOf("\\{", StringComparison.Ordinal);
        if (interfaceGuid >= 0) instance = instance[..interfaceGuid];
        if (CM_Locate_DevNode(out uint device, instance, 0) != 0) return false;
        for (int depth = 0; depth < 8; depth++)
        {
            var value = new StringBuilder(1024);
            if (CM_Get_Device_ID(device, value, value.Capacity, 0) != 0) return false;
            string current = value.ToString();
            if (current.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase)) { identity = current; return true; }
            if (CM_Get_Parent(out uint parent, device, 0) != 0) return false;
            device = parent;
        }
        return false;
    }

    private sealed record RawDevice(nint Handle, string Path, string Fingerprint, string VendorId, string ProductId, string Name, string IdentityConfidence);
    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public nint Target; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList { public nint Device; public uint Type; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader { public uint Type; public uint Size; public nint Device; public nint WParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard { public ushort MakeCode; public ushort Flags; public ushort Reserved; public ushort VirtualKey; public uint Message; public uint ExtraInformation; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([In, Out] RawInputDeviceList[]? devices, ref uint count, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(nint device, uint command, StringBuilder? data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint rawInput, uint command, nint data, ref uint size, uint headerSize);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out uint deviceInstance, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(uint deviceInstance, StringBuilder buffer, int bufferLength, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parentDeviceInstance, uint deviceInstance, uint flags);
}
