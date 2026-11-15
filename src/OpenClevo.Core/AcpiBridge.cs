using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenClevo.Core;

/// <summary>
/// Talks to the Clevo "Acpi Bridge" kernel driver (ACPI\CLV0001, AcpiBridge.sys) that ships with
/// Clevo Control Center. Re-implements the wire format used by InsydeDCHU.dll (reverse engineered).
///
/// IOCTL 0x322400: evaluate the firmware _DSM with GUID 93f224e4-fbdc-4bbf-add6-db71bdc0afad.
///   input (0x420 bytes):
///     +0x00 GUID (16)          +0x10 u32 0            +0x14 u32 '_DSM' (0x4D53445F)
///     +0x18 u32 function/cmd   +0x1C u16 0x0104       +0x1E u16 2 (type = buffer)
///     +0x20 u16 0x0100 (len)   +0x22 data[256]
///   output (0x40C bytes): ACPI_EVAL_OUTPUT_BUFFER (sig, length, count, args[ type u16, len u16, data ])
///     arg type 0 = integer (u32), type 2 = buffer.
/// IOCTL 0x32240C: 4 KiB "AppSettings" store kept by the driver/firmware (page * 0x100 + offset).
/// </summary>
public sealed class AcpiBridge : IDisposable
{
    public static readonly Guid InterfaceGuid = new("86994c74-ad43-4812-b7e7-0c420b5c5fd7");
    public static readonly Guid DsmGuid = new("93f224e4-fbdc-4bbf-add6-db71bdc0afad");

    const uint IoctlEvalDsm = 0x322400;
    const uint IoctlAppSettings = 0x32240C;
    const int DsmInSize = 0x420, DsmOutSize = 0x40C, AppSettingsSize = 0x1000;

    readonly SafeFileHandle _h;
    readonly object _lock = new();

    AcpiBridge(SafeFileHandle h) => _h = h;

    public static string FindDevicePath()
    {
        Guid g = InterfaceGuid;
        int cr = CM_Get_Device_Interface_List_SizeW(out uint len, ref g, null, 0);
        if (cr != 0 || len <= 1) throw new InvalidOperationException($"AcpiBridge interface not found (CR={cr}). Is Clevo's Acpi Bridge driver installed?");
        var buf = new char[len];
        cr = CM_Get_Device_Interface_ListW(ref g, null, buf, len, 0);
        if (cr != 0) throw new InvalidOperationException($"CM_Get_Device_Interface_List failed (CR={cr})");
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    public static AcpiBridge Open()
    {
        var path = FindDevicePath();
        var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFile({path})");
        return new AcpiBridge(h);
    }

    /// <summary>Raw _DSM call. Returns the parsed output arguments.</summary>
    public DsmResult Call(int function, ReadOnlySpan<byte> data = default)
    {
        if (data.Length > 256) throw new ArgumentOutOfRangeException(nameof(data));
        var inBuf = new byte[DsmInSize];
        DsmGuid.TryWriteBytes(inBuf.AsSpan(0, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(inBuf.AsSpan(0x14), 0x4D53445F);
        BinaryPrimitives.WriteInt32LittleEndian(inBuf.AsSpan(0x18), function);
        BinaryPrimitives.WriteUInt16LittleEndian(inBuf.AsSpan(0x1C), 0x0104);
        BinaryPrimitives.WriteUInt16LittleEndian(inBuf.AsSpan(0x1E), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(inBuf.AsSpan(0x20), 0x100);
        data.CopyTo(inBuf.AsSpan(0x22));

        var outBuf = new byte[DsmOutSize];
        lock (_lock)
        {
            if (!DeviceIoControl(_h, IoctlEvalDsm, inBuf, inBuf.Length, outBuf, outBuf.Length, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"_DSM function {function}");
        }
        return DsmResult.Parse(outBuf);
    }

    /// <summary>Equivalent of GetDCHU_Data_Integer.</summary>
    public uint GetInteger(int function) => Call(function).Integer ?? 0;

    /// <summary>Equivalent of GetDCHU_Data_Buffer.</summary>
    public byte[] GetBuffer(int function) => Call(function).Buffer ?? new byte[256];

    /// <summary>Equivalent of ReadAppSettings: reads the full 4 KiB store.</summary>
    public byte[] ReadAppSettingsStore()
    {
        var inBuf = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(inBuf.AsSpan(4), 0x10000000);
        var outBuf = new byte[AppSettingsSize];
        lock (_lock)
        {
            if (!DeviceIoControl(_h, IoctlAppSettings, inBuf, inBuf.Length, outBuf, outBuf.Length, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadAppSettings");
        }
        return outBuf;
    }

    /// <summary>Equivalent of WriteAppSettings: in = { u32 1, u16 page*0x100+offset, u16 length, data[0x1000] }.</summary>
    public void WriteAppSettings(int page, int offset, ReadOnlySpan<byte> data)
    {
        int addr = page * 0x100 + offset;
        if (data.Length == 0 || addr + data.Length > AppSettingsSize) throw new ArgumentOutOfRangeException(nameof(data));
        var inBuf = new byte[8 + AppSettingsSize];
        BinaryPrimitives.WriteUInt32LittleEndian(inBuf, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(inBuf.AsSpan(4), (ushort)addr);
        BinaryPrimitives.WriteUInt16LittleEndian(inBuf.AsSpan(6), (ushort)data.Length);
        data.CopyTo(inBuf.AsSpan(8));
        lock (_lock)
        {
            if (!DeviceIoControl(_h, IoctlAppSettings, inBuf, inBuf.Length, null, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteAppSettings");
        }
    }

    public byte[] ReadAppSettings(int page, int offset, int length) =>
        ReadAppSettingsStore().AsSpan(page * 0x100 + offset, length).ToArray();

    public void Dispose() => _h.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inLen, byte[]? outBuf, int outLen, out int returned, IntPtr ov);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid cls, string? devId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid cls, string? devId, char[] buf, uint len, uint flags);
}

public sealed record DsmResult(uint Signature, int Count, uint? Integer, byte[]? Buffer)
{
    public static DsmResult Parse(byte[] o)
    {
        uint sig = BinaryPrimitives.ReadUInt32LittleEndian(o);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan(8));
        uint? integer = null; byte[]? buffer = null;
        int p = 12;
        for (int i = 0; i < count && p + 4 <= o.Length; i++)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(o.AsSpan(p));
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(o.AsSpan(p + 2));
            int n = Math.Min(len, o.Length - p - 4);
            if (type == 0 && n >= 4) integer = BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan(p + 4));
            else if (type == 2) buffer = o.AsSpan(p + 4, n).ToArray();
            p += 4 + Math.Max((int)len, 4);
        }
        return new DsmResult(sig, count, integer, buffer);
    }
}
