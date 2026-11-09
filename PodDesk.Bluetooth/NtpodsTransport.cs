using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PodDesk.Bluetooth;

// Talks directly to the optional NTPodsAAP profile driver. Only opens a PnP
// interface belonging to the selected classic Bluetooth address.
public sealed class NtpodsTransport : IDisposable
{
    private static readonly Guid InterfaceClass = new("C0FFEE00-1337-4A5B-9E6F-A1B2C3D4E5F6");
    private readonly SafeFileHandle handle;
    private const uint ConnectCode = 0x80002000;
    private const uint SendCode = 0x80002008;
    private const uint ReceiveCode = 0x8000200C;
    private const uint StatusCode = 0x80002010;

    private NtpodsTransport(SafeFileHandle handle) => this.handle = handle;

    public static string? FindInterface(ulong address)
    {
        var addressPart = $"&{address:X12}_";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out var length, in InterfaceClass, null, 0) != 0 || length <= 1)
                return null;
            var buffer = new char[length];
            var result = CM_Get_Device_Interface_ListW(in InterfaceClass, null, buffer, length, 0);
            if (result == 0)
                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(path => path.Contains(WindowsBluetooth.AapService, StringComparison.OrdinalIgnoreCase)
                        && path.Contains(addressPart, StringComparison.OrdinalIgnoreCase));
            if (result != 0x1A) return null; // CR_BUFFER_SMALL: PnP list changed.
        }
        return null;
    }

    public static NtpodsTransport Open(string path)
    {
        var file = CreateFileW(path, 0xC0000000, 0, 0, 3, 0, 0);
        if (!file.IsInvalid) return new(file);
        var error = Marshal.GetLastWin32Error();
        file.Dispose();
        throw new Win32Exception(error, $"Cannot open NTPodsAAP (Windows error {error}). Close any other AirPods control app.");
    }

    public void Connect(ulong address)
    {
        var input = new byte[10];
        BinaryPrimitives.WriteUInt64LittleEndian(input, address);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(8), AapProtocol.Psm);
        var result = new byte[8];
        if (Ioctl(ConnectCode, input, result) < 8 || BinaryPrimitives.ReadUInt32LittleEndian(result) == 0)
            throw new IOException($"AirPods declined the L2CAP channel (0x{BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4)):X8}).");
        if (!IsConnectedTo(address)) throw new IOException("The control link did not report the selected address.");
    }

    public bool IsConnectedTo(ulong address)
    {
        var buffer = new byte[32];
        return Ioctl(StatusCode, [], buffer) >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(buffer) == 2 &&
            BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(4)) == address;
    }

    public void Send(byte[] packet) => Ioctl(SendCode, packet, []);

    public int Receive(byte[] buffer, uint timeoutMs)
    {
        var timeout = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(timeout, timeoutMs);
        try { return Ioctl(ReceiveCode, timeout, buffer); }
        catch (Win32Exception error) when (error.NativeErrorCode is 121 or 258 or 1460) { return 0; }
    }

    private int Ioctl(uint code, byte[] input, byte[] output)
    {
        if (DeviceIoControl(handle, code, input, (uint)input.Length, output, (uint)output.Length,
            out var read, 0)) return (int)read;
        throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose() => handle.Dispose();

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, in Guid guid, string? instance, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(in Guid guid, string? instance,
        [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputLength,
        [Out] byte[] output, uint outputLength, out uint transferred, nint overlapped);
}
