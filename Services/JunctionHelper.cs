using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UnturnedModLoader.Services;

/// <summary>NTFS directory junction helpers (no admin required).</summary>
public static class JunctionHelper
{
    private const string NonInterpretedPathPrefix = @"\??\";
    private const int GenericWrite = 0x40000000;
    private const int OpenExisting = 3;
    private const int FileFlagOpenReparsePoint = 0x00200000;
    private const int FileFlagBackupSemantics = 0x02000000;
    private const int FsctlSetReparsePoint = 0x000900A4;
    private const int FsctlGetReparsePoint = 0x000900A8;
    private const int FsctlDeleteReparsePoint = 0x000900AC;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const int ErrorNotAReparsePoint = 4390;

    public static bool IsJunction(string path)
    {
        if (!Directory.Exists(path))
            return false;

        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReparsePoint) == 0)
            return false;

        return TryGetJunctionTarget(path, out _);
    }

    public static bool TryGetJunctionTarget(string path, out string? target)
    {
        target = null;
        if (!Directory.Exists(path))
            return false;

        using var handle = OpenReparsePoint(path, isWrite: false);
        if (handle.IsInvalid)
            return false;

        var buffer = new byte[16 * 1024];
        if (!DeviceIoControl(handle, FsctlGetReparsePoint, null, 0, buffer, buffer.Length, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotAReparsePoint)
                return false;
            return false;
        }

        var tag = BitConverter.ToUInt32(buffer, 0);
        if (tag != IoReparseTagMountPoint)
            return false;

        var printNameOffset = BitConverter.ToUInt16(buffer, 12);
        var printNameLength = BitConverter.ToUInt16(buffer, 14);
        var pathBufferStart = 16;
        target = Encoding.Unicode.GetString(buffer, pathBufferStart + printNameOffset, printNameLength);
        return !string.IsNullOrWhiteSpace(target);
    }

    public static void CreateJunction(string junctionPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Junctions are only supported on Windows.");

        targetPath = Path.GetFullPath(targetPath);
        junctionPath = Path.GetFullPath(junctionPath);

        if (!Directory.Exists(targetPath))
            Directory.CreateDirectory(targetPath);

        if (Directory.Exists(junctionPath) || File.Exists(junctionPath))
            throw new IOException($"Path already exists: {junctionPath}");

        Directory.CreateDirectory(junctionPath);

        var substituteName = NonInterpretedPathPrefix + targetPath;
        var printName = targetPath;

        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(printName);

        var pathBuffer = new byte[substituteBytes.Length + printBytes.Length + 4];
        Buffer.BlockCopy(substituteBytes, 0, pathBuffer, 0, substituteBytes.Length);
        Buffer.BlockCopy(printBytes, 0, pathBuffer, substituteBytes.Length + 2, printBytes.Length);

        var headerSize = 8;
        var reparseDataLength = (ushort)(12 + pathBuffer.Length);
        var buffer = new byte[headerSize + reparseDataLength];

        BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
        BitConverter.GetBytes(reparseDataLength).CopyTo(buffer, 4);
        // Reserved uint16 at offset 6
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8); // SubstituteNameOffset
        BitConverter.GetBytes((ushort)substituteBytes.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substituteBytes.Length + 2)).CopyTo(buffer, 12); // PrintNameOffset
        BitConverter.GetBytes((ushort)printBytes.Length).CopyTo(buffer, 14);
        Buffer.BlockCopy(pathBuffer, 0, buffer, 16, pathBuffer.Length);

        using var handle = OpenReparsePoint(junctionPath, isWrite: true);
        if (handle.IsInvalid)
            throw new IOException($"Cannot open junction path: {junctionPath}", Marshal.GetLastWin32Error());

        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, null, 0, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            try { Directory.Delete(junctionPath); } catch { /* ignore */ }
            throw new IOException($"Failed to create junction from '{junctionPath}' to '{targetPath}'.", error);
        }
    }

    public static void DeleteJunction(string junctionPath)
    {
        if (!IsJunction(junctionPath))
            throw new IOException($"Not a junction: {junctionPath}");

        // Directory.Delete removes the reparse point without deleting the target.
        Directory.Delete(junctionPath);
    }

    private static SafeFileHandle OpenReparsePoint(string path, bool isWrite)
    {
        var access = isWrite ? GenericWrite : 0;
        var handle = CreateFile(
            path,
            access,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        return new SafeFileHandle(handle, ownsHandle: true);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        int dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        int dwCreationDisposition,
        int dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        int dwIoControlCode,
        byte[]? lpInBuffer,
        int nInBufferSize,
        byte[]? lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
