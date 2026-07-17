using Fsp;
using Fsp.Interop;
using System.Collections;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using UnturnedModLoader.Models;
using UnturnedModLoader.Services.WinFsp;
using FileInfo = Fsp.Interop.FileInfo;

namespace UnturnedModLoader.Services;

/// <summary>
/// WinFsp-backed virtual filesystem that merges a profile "overlay" (upper layer)
/// over the real game install (lower layer) and presents the merged tree at a
/// drive letter (e.g. <c>U:</c>). The game launches from this virtual drive.
///
/// The volume is mounted for the entire loader process lifetime. Switching the
/// active profile or the game path only swaps the upper/lower root pointers
/// atomically; it does not remount the volume.
///
/// Layer semantics: a path is served from the upper layer when it exists there,
/// otherwise from the lower layer. Writes (Phase 3) copy a lower-layer file up
/// into the upper layer before mutating it.
/// </summary>
public sealed class VirtualFilesystemService : IDisposable
{
    private const ulong AllocationUnit = 4096;

    /// <summary>
    /// Maximum legal Win32 FILETIME (9999-12-31 23:59:59 UTC). Values outside
    /// [0, MaxFileTime] make <see cref="DateTime.FromFileTimeUtc"/> throw
    /// ArgumentOutOfRangeException, crashing callers like the BepInEx preloader that
    /// read a file's LastWriteTime. Clamp anything out of range to 0 ("unknown").
    /// </summary>
    private const ulong MaxFileTime = 265046774399999999;

    private static ulong ToFileTimeSafe(DateTime time)
    {
        if (time.Kind == DateTimeKind.Local)
            time = time.ToUniversalTime();

        // ToFileTimeUtc throws on dates before 1601-01-01 (e.g. DateTime(0)); treat those
        // as "no time" rather than propagating an exception up through WinFsp.
        if (time.Year < 1601)
            return 0;

        try
        {
            var ft = (ulong)time.ToFileTimeUtc();
            return ft > MaxFileTime ? 0 : ft;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Clamp a raw FILETIME (e.g. from <see cref="GetFileInformationByHandle"/>) into the
    /// legal [0, MaxFileTime] range so consumers cannot blow up on FromFileTimeUtc.
    /// </summary>
    private static ulong ClampFileTime(ulong fileTime) =>
        fileTime == 0 || fileTime > MaxFileTime ? 0 : fileTime;

    private readonly OverlayFilesystem _fs = new();
    private FileSystemHost? _host;
    private char _driveLetter;
    private bool _disposed;

    public bool IsMounted => _host is not null && _driveLetter != 0;
    public char DriveLetter => _driveLetter;
    public string? MountPoint => IsMounted ? $"{_driveLetter}:" : null;

    /// <summary>
    /// Mounts the virtual volume at a free drive letter. Returns
    /// <see cref="MountResult.Fail"/> (non-fatal) when WinFsp is not installed
    /// or no drive letter is free.
    /// </summary>
    public MountResult Mount()
    {
        if (!OperatingSystem.IsWindows())
            return MountResult.Fail("Virtual filesystem requires Windows.");

        if (IsMounted)
            return MountResult.Ok();

        var status = WinFspService.GetStatus();
        if (status.State != WinFspInstallState.Installed)
            return MountResult.Fail(status.Detail);

        var drive = DriveLetterClaimer.FindFree();
        if (drive is null)
            return MountResult.Fail("No free drive letter available for the virtual drive.");

        _host = new FileSystemHost(_fs);
        _host.SectorSize = 4096;
        _host.SectorsPerAllocationUnit = 1;
        _host.MaxComponentLength = 255;
        _host.FileInfoTimeout = 1000; // 1s metadata cache - key performance lever
        _host.CasePreservedNames = true;
        _host.UnicodeOnDisk = true;
        _host.PersistentAcls = true;
        _host.FileSystemName = "UnturnedModLoader";

        // MountPoint must be in "X:" form (no trailing backslash) per WinFsp convention.
        var nt = _host.Mount(drive.Value + ":", null, false, 0);
        if (nt != 0)
        {
            _host.Dispose();
            _host = null;
            return MountResult.Fail($"WinFsp mount failed (NTSTATUS 0x{unchecked((uint)nt):X8}).");
        }

        _driveLetter = drive.Value;
        return MountResult.Ok();
    }

    /// <summary>Atomically swaps the upper (profile overlay) root.</summary>
    public void SetActiveOverlay(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            _fs.SetUpperRoot(null);
            return;
        }

        AppPaths.EnsureProfileLayout(profileId);
        _fs.SetUpperRoot(AppPaths.ProfileOverlayDir(profileId));
    }

    /// <summary>
    /// Atomically swaps the upper (profile overlay) root to an explicit directory.
    /// Diagnostic hook used when the upper root is not a managed profile overlay
    /// (e.g. tests). <see cref="SetActiveOverlay"/> is the normal entry point.
    /// </summary>
    public void SetUpperRoot(string? overlayDir) => _fs.SetUpperRoot(overlayDir);

    /// <summary>Atomically swaps the lower (real game install) root.</summary>
    public void SetLowerRoot(string? gamePath) =>
        _fs.SetLowerRoot(string.IsNullOrWhiteSpace(gamePath) ? null : gamePath);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (_host is not null)
            {
                _host.Unmount();
                _host.Dispose();
            }
        }
        catch
        {
            // WinFsp auto-unmounts when the host process exits; best-effort here.
        }
        finally
        {
            _host = null;
            _driveLetter = '\0';
        }
    }

    /// <summary>
    /// Per-open state. Binds a WinFsp FileContext to a backing <see cref="FileStream"/>
    /// or <see cref="DirectoryInfo"/> on the resolved layer. <see cref="IsUpper"/>
    /// records which layer backs the handle so Phase 3 copy-up knows whether to
    /// promote before writing. <see cref="FileName"/> is the WinFsp-normalized path
    /// (e.g. <c>\Modules\foo.dll</c>), carried so <see cref="OverlayFilesystem.Cleanup"/>
    /// can act on the CleanupDelete flag.
    /// </summary>
    private sealed class FileDesc
    {
        public FileStream? Stream;
        public DirectoryInfo? DirInfo;
        public bool IsUpper;
        public string BackingPath = "";
        public string FileName = "";

        // Set by CanDelete when the kernel marks the file for deletion; honored in Cleanup.
        public bool DeleteOnCleanup;

        // Lazily populated directory entry list for ReadDirectoryEntry paging.
        public DictionaryEntry[]? FileSystemInfos;

        public FileDesc(FileStream stream, bool isUpper, string backingPath, string fileName)
        {
            Stream = stream;
            IsUpper = isUpper;
            BackingPath = backingPath;
            FileName = fileName;
        }

        public FileDesc(DirectoryInfo dir, bool isUpper, string backingPath, string fileName)
        {
            DirInfo = dir;
            IsUpper = isUpper;
            BackingPath = backingPath;
            FileName = fileName;
        }

        public bool IsDirectory => DirInfo is not null;

        public Int32 GetFileInfo(out FileInfo fileInfo)
        {
            if (Stream is not null)
            {
                if (!GetFileInformationByHandle(Stream.SafeFileHandle,
                        out var info))
                {
                    fileInfo = default;
                    return unchecked((Int32)0xC0000001); // STATUS_UNSUCCESSFUL
                }

                fileInfo = default;
                fileInfo.FileAttributes = info.dwFileAttributes;
                fileInfo.ReparseTag = 0;
                fileInfo.FileSize = (ulong)Stream.Length;
                fileInfo.AllocationSize = (fileInfo.FileSize + AllocationUnit - 1)
                    / AllocationUnit * AllocationUnit;
                fileInfo.CreationTime = ClampFileTime(info.ftCreationTime);
                fileInfo.LastAccessTime = ClampFileTime(info.ftLastAccessTime);
                fileInfo.LastWriteTime = ClampFileTime(info.ftLastWriteTime);
                fileInfo.ChangeTime = fileInfo.LastWriteTime;
                fileInfo.IndexNumber = 0;
                fileInfo.HardLinks = 0;
                return 0;
            }

            GetFileInfoFromFileSystemInfo(DirInfo!, out fileInfo);
            return 0;
        }

        public static void GetFileInfoFromFileSystemInfo(FileSystemInfo info, out FileInfo fileInfo)
        {
            fileInfo = default;
            fileInfo.FileAttributes = (uint)info.Attributes;
            fileInfo.ReparseTag = 0;
            fileInfo.FileSize = info is System.IO.FileInfo f ? (ulong)f.Length : 0;
            fileInfo.AllocationSize = (fileInfo.FileSize + AllocationUnit - 1)
                / AllocationUnit * AllocationUnit;
            fileInfo.CreationTime = ToFileTimeSafe(info.CreationTimeUtc);
            fileInfo.LastAccessTime = ToFileTimeSafe(info.LastAccessTimeUtc);
            fileInfo.LastWriteTime = ToFileTimeSafe(info.LastWriteTimeUtc);
            fileInfo.ChangeTime = fileInfo.LastWriteTime;
            fileInfo.IndexNumber = 0;
            fileInfo.HardLinks = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BY_HANDLE_FILE_INFORMATION
        {
            public uint dwFileAttributes;
            public ulong ftCreationTime;
            public ulong ftLastAccessTime;
            public ulong ftLastWriteTime;
            public uint dwVolumeSerialNumber;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint nNumberOfLinks;
            public uint nFileIndexHigh;
            public uint nFileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
            out BY_HANDLE_FILE_INFORMATION lpFileInformation);
    }

    /// <summary>
    /// The <see cref="FileSystemBase"/> implementation. Upper/lower roots are
    /// volatile fields swapped atomically; every callback reads them at entry.
    /// Phase 2 implements the read path (overlay merge). Write callbacks are
    /// filled in by Phase 3.
    /// </summary>
    private sealed class OverlayFilesystem : FileSystemBase
    {
        private volatile string? _upperRoot;
        private volatile string? _lowerRoot;

        public void SetUpperRoot(string? path) =>
            System.Threading.Interlocked.Exchange(ref _upperRoot, path);
        public void SetLowerRoot(string? path) =>
            System.Threading.Interlocked.Exchange(ref _lowerRoot, path);

        public override Int32 ExceptionHandler(Exception ex) => STATUS_SUCCESS;

        public override Int32 GetVolumeInfo(out VolumeInfo volumeInfo)
        {
            volumeInfo = new VolumeInfo
            {
                TotalSize = 64UL * 1024 * 1024 * 1024,
                FreeSize = 32UL * 1024 * 1024 * 1024,
            };
            return STATUS_SUCCESS;
        }

        public override Int32 GetSecurityByName(
            string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
        {
            if (!TryResolve(fileName, out var path, out var isUpper))
            {
                fileAttributes = 0;
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            // Attributes via FileSystemInfo (works for both file and directory).
            var info = new System.IO.FileInfo(path!);
            fileAttributes = (uint)info.Attributes;
            // Security descriptor left to WinFsp defaults (PersistentAcls handles ACLs lazily).
            return STATUS_SUCCESS;
        }

        public override Int32 Open(
            string fileName, uint createOptions, uint grantedAccess,
            out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
        {
            fileNode = null!;
            normalizedName = null!;

            if (!TryResolve(fileName, out var path, out var isUpper) || path is null)
            {
                fileDesc = null!;
                fileInfo = default;
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    var fd = new FileDesc(new DirectoryInfo(path), isUpper, path, fileName);
                    fileDesc = fd;
                    return fd.GetFileInfo(out fileInfo);
                }
                else
                {
                    var access = GrantedAccessToFileAccess(grantedAccess);
                    var fs = new FileStream(
                        path,
                        FileMode.Open,
                        access,
                        FileShare.Read | FileShare.Write | FileShare.Delete,
                        4096,
                        FileOptions.None);
                    var fd = new FileDesc(fs, isUpper, path, fileName);
                    fileDesc = fd;
                    return fd.GetFileInfo(out fileInfo);
                }
            }
            catch (Exception)
            {
                fileDesc = null!;
                fileInfo = default;
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }
        }

        public override void Close(object fileNode, object fileDesc0)
        {
            var fd = (FileDesc)fileDesc0;
            fd.Stream?.Dispose();
        }

        public override Int32 Read(
            object fileNode, object fileDesc0,
            IntPtr buffer, ulong offset, uint length, out uint pBytesTransferred)
        {
            var fd = (FileDesc)fileDesc0;
            if (fd.Stream is null)
            {
                pBytesTransferred = 0;
                return STATUS_INVALID_DEVICE_REQUEST;
            }

            var stream = fd.Stream;
            try
            {
                if ((ulong)stream.Length <= offset)
                {
                    pBytesTransferred = 0;
                    return STATUS_END_OF_FILE;
                }

                stream.Seek((long)offset, SeekOrigin.Begin);
                var bytes = new byte[length];
                var read = stream.Read(bytes, 0, (int)length);
                if (read > 0)
                    Marshal.Copy(bytes, 0, buffer, read);
                pBytesTransferred = (uint)read;
                return STATUS_SUCCESS;
            }
            catch (Exception)
            {
                pBytesTransferred = 0;
                return STATUS_ACCESS_DENIED;
            }
        }

        public override Int32 GetFileInfo(object fileNode, object fileDesc0, out FileInfo fileInfo)
        {
            var fd = (FileDesc)fileDesc0;
            return fd.GetFileInfo(out fileInfo);
        }

        // Directory enumeration via the high-level ReadDirectoryEntry callback.
        // FileSystemBase's default ReadDirectory fills the buffer from these entries.
        public override Boolean ReadDirectoryEntry(
            object fileNode, object fileDesc0,
            string pattern, string marker,
            ref object context,
            out string fileName, out FileInfo fileInfo)
        {
            var fd = (FileDesc)fileDesc0;
            fileInfo = default;
            fileName = null!;

            if (fd.FileSystemInfos is null)
            {
                var upper = _upperRoot;
                var lower = _lowerRoot;
                var merged = new SortedList(StringComparer.OrdinalIgnoreCase);

                // Emit "." / ".." for the merged directory.
                var selfInfo = new DirectoryInfo(fd.BackingPath);
                if (selfInfo.Parent is not null)
                {
                    AddEntry(merged, ".", selfInfo);
                    AddEntry(merged, "..", selfInfo.Parent);
                }

                // Layer-relative path of this directory (from the WinFsp fileName, which is
                // layer-agnostic). "" = the volume root. Used so the same logical directory can
                // be enumerated in both layers even when the open handle backs only one of them.
                var dirRel = NormalizeRelative(fd.FileName) ?? "";

                // Upper entries shadow lower entries by name.
                if (upper is not null)
                    EnumerateLayer(merged, upper, dirRel);
                if (lower is not null)
                    EnumerateLayer(merged, lower, dirRel);

                fd.FileSystemInfos = new DictionaryEntry[merged.Count];
                merged.CopyTo(fd.FileSystemInfos, 0);
            }

            int index;
            if (context is null)
            {
                index = 0;
                if (marker is not null)
                {
                    index = Array.FindIndex(fd.FileSystemInfos,
                        e => string.Equals((string)e.Key, marker, StringComparison.OrdinalIgnoreCase));
                    index = index < 0 ? fd.FileSystemInfos.Length : index + 1;
                }
            }
            else
            {
                index = (int)context + 1;
            }

            if (index >= fd.FileSystemInfos.Length)
            {
                context = null!;
                return false;
            }

            var entry = fd.FileSystemInfos[index];
            context = index;
            fileName = (string)entry.Key;
            var info = (FileSystemInfo)entry.Value!;
            FileDesc.GetFileInfoFromFileSystemInfo(info, out fileInfo);
            return true;
        }

        private static void AddEntry(SortedList list, string name, FileSystemInfo info)
        {
            if (!list.Contains(name))
                list.Add(name, info);
        }

        /// <param name="dirRel">Layer-relative path of the directory to enumerate ("=" = root).</param>
        private static void EnumerateLayer(SortedList merged, string layerRoot, string dirRel)
        {
            var layerFull = Path.GetFullPath(layerRoot).TrimEnd(Path.DirectorySeparatorChar);
            var layerDir = string.IsNullOrEmpty(dirRel)
                ? new DirectoryInfo(layerFull)
                : new DirectoryInfo(Path.Combine(layerFull, dirRel));
            if (!layerDir.Exists)
                return;

            foreach (var entry in layerDir.EnumerateFileSystemInfos())
            {
                if (!merged.Contains(entry.Name))
                    merged.Add(entry.Name, entry);
            }
        }

        /// <summary>
        /// Resolves a WinFsp file name (e.g. <c>\Modules\foo.dll</c>) to the
        /// backing absolute path, preferring the upper layer. Returns false when
        /// the path exists in neither layer.
        /// </summary>
        private bool TryResolve(string fileName, out string? path, out bool isUpper)
        {
            path = null;
            isUpper = false;

            var rel = NormalizeRelative(fileName);
            if (rel is null)
                return false;

            var upper = _upperRoot;
            var lower = _lowerRoot;

            if (upper is not null)
            {
                var candidate = string.IsNullOrEmpty(rel) ? upper : Path.Combine(upper, rel);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    path = candidate;
                    isUpper = true;
                    return true;
                }
            }

            if (lower is not null)
            {
                var candidate = string.IsNullOrEmpty(rel) ? lower : Path.Combine(lower, rel);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    path = candidate;
                    isUpper = false;
                    return true;
                }
            }

            return false;
        }

        private static string? NormalizeRelative(string fileName)
        {
            var rel = fileName.Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            if (rel.Contains("..", StringComparison.Ordinal))
                return null;
            return rel;
        }

        /// <summary>
        /// Maps a WinFsp GrantedAccess mask (FILE_READ_DATA / FILE_WRITE_DATA etc.)
        /// to a managed <see cref="FileAccess"/>. Generic read and execute map to Read;
        /// anything with write maps to ReadWrite.
        /// </summary>
        private static FileAccess GrantedAccessToFileAccess(uint grantedAccess)
        {
            const uint FILE_WRITE_DATA = 0x00000002;
            const uint FILE_APPEND_DATA = 0x00000004;
            const uint DELETE = 0x00010000;
            const uint WRITE_DAC = 0x00040000;
            const uint WRITE_OWNER = 0x00080000;

            if ((grantedAccess & (FILE_WRITE_DATA | FILE_APPEND_DATA | DELETE | WRITE_DAC | WRITE_OWNER)) != 0)
                return FileAccess.ReadWrite;
            return FileAccess.Read;
        }

        // ---- Write path: overlay copy-up semantics. All mutations target the
        // upper layer; a lower-backed file is copied up to the overlay before the
        // first mutation. Ancestors are materialized in the upper layer on demand. ----

        /// <summary>
        /// Resolves <paramref name="fileName"/> to its absolute path in the upper
        /// layer, creating ancestor directories as needed. Use for Create/Rename
        /// targets. Does <b>not</b> require the path to already exist.
        /// </summary>
        private string? ResolveUpperTarget(string fileName)
        {
            var rel = NormalizeRelative(fileName);
            if (rel is null)
                return null;

            var upper = _upperRoot;
            if (upper is null)
                return null;

            var target = string.IsNullOrEmpty(rel) ? upper : Path.Combine(upper, rel);
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            return target;
        }

        /// <summary>
        /// Promotes a lower-backed file into the upper layer (copy-up): copies the
        /// backing file to its upper-layer location and rebinds <paramref name="fd"/>
        /// to a writable handle on the upper copy. No-op when already upper-backed.
        /// </summary>
        private void CopyUp(FileDesc fd)
        {
            if (fd.IsUpper || fd.Stream is null)
                return;

            var upper = _upperRoot;
            if (upper is null)
                return;

            var dest = string.IsNullOrEmpty(fd.FileName) || fd.FileName == "\\"
                ? upper
                : Path.Combine(upper, NormalizeRelative(fd.FileName)!);

            var parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            // Close the read handle on the lower file, copy bytes up, reopen on the
            // upper copy with read/write so subsequent writes land in the overlay.
            var oldPosition = fd.Stream.Position;
            fd.Stream.Dispose();

            File.Copy(fd.BackingPath, dest, overwrite: true);

            fd.Stream = new FileStream(
                dest,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                4096,
                FileOptions.None);
            fd.BackingPath = dest;
            fd.IsUpper = true;
            try { fd.Stream.Position = oldPosition; }
            catch { /* seek may be out of range after truncation; ignore */ }
        }

        public override Int32 Create(
            string fileName, uint createOptions, uint grantedAccess,
            uint fileAttributes, byte[] securityDescriptor, ulong allocationSize,
            out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
        {
            fileNode = null!;
            normalizedName = fileName;
            fileInfo = default;

            var upper = _upperRoot;
            if (upper is null)
            {
                fileDesc = null!;
                return STATUS_ACCESS_DENIED; // no overlay to write into
            }

            var target = ResolveUpperTarget(fileName);
            if (target is null)
            {
                fileDesc = null!;
                return STATUS_OBJECT_NAME_INVALID;
            }

            var isDirectory = (createOptions & FILE_DIRECTORY_FILE) != 0
                              || (fileAttributes & (uint)System.IO.FileAttributes.Directory) != 0;

            try
            {
                FileStream? fs = null;
                DirectoryInfo? dir = null;
                if (isDirectory)
                {
                    Directory.CreateDirectory(target);
                    dir = new DirectoryInfo(target);
                }
                else
                {
                    // A new file is implicitly an overlay file (archive bit, default attrs).
                    var attr = (System.IO.FileAttributes)fileAttributes;
                    if ((attr & System.IO.FileAttributes.Directory) != 0)
                        attr &= ~System.IO.FileAttributes.Directory;
                    if (attr == 0)
                        attr = System.IO.FileAttributes.Archive;

                    fs = new FileStream(
                        target,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.Read | FileShare.Write | FileShare.Delete,
                        4096,
                        FileOptions.None);
                    File.SetAttributes(target, attr);
                }

                FileDesc fd;
                if (fs is not null)
                    fd = new FileDesc(fs, isUpper: true, target, fileName);
                else
                    fd = new FileDesc(dir!, isUpper: true, target, fileName);
                fileDesc = fd;
                return fd.GetFileInfo(out fileInfo);
            }
            catch (Exception)
            {
                fileDesc = null!;
                fileInfo = default;
                return STATUS_OBJECT_NAME_COLLISION;
            }
        }

        public override Int32 Write(
            object fileNode, object fileDesc0,
            IntPtr buffer, ulong offset, uint length,
            bool writeToEndOfFile, bool constrainedIo,
            out uint pBytesWritten, out FileInfo fileInfo)
        {
            pBytesWritten = 0;
            fileInfo = default;

            var fd = (FileDesc)fileDesc0;
            if (fd.Stream is null)
                return STATUS_INVALID_DEVICE_REQUEST;
            if (_upperRoot is null)
                return STATUS_ACCESS_DENIED;

            CopyUp(fd);
            var stream = fd.Stream;

            try
            {
                long writeOffset;
                if (constrainedIo)
                {
                    // Must not extend the file: clamp the range to current size.
                    if ((ulong)stream.Length <= offset)
                    {
                        pBytesWritten = 0;
                        return fd.GetFileInfo(out fileInfo);
                    }
                    writeOffset = (long)offset;
                    var end = Math.Min((long)offset + length, stream.Length);
                    length = (uint)Math.Max(0, end - writeOffset);
                    if (length == 0)
                    {
                        pBytesWritten = 0;
                        return fd.GetFileInfo(out fileInfo);
                    }
                }
                else if (writeToEndOfFile)
                {
                    writeOffset = stream.Length;
                }
                else
                {
                    writeOffset = (long)offset;
                }

                stream.Seek(writeOffset, SeekOrigin.Begin);
                var bytes = new byte[length];
                Marshal.Copy(buffer, bytes, 0, (int)length);
                stream.Write(bytes, 0, (int)length);
                // Flush to the backing file immediately so persisted data is visible even before
                // the (possibly deferred) Cleanup/Close. WinFsp may delay Cleanup past the caller's
                // CloseHandle return; this keeps the overlay file consistent on disk at all times.
                stream.Flush(flushToDisk: true);

                pBytesWritten = length;
                return fd.GetFileInfo(out fileInfo);
            }
            catch (Exception)
            {
                pBytesWritten = 0;
                return STATUS_ACCESS_DENIED;
            }
        }

        public override Int32 Overwrite(
            object fileNode, object fileDesc0,
            uint fileAttributes, bool replaceFileAttributes, ulong allocationSize,
            out FileInfo fileInfo)
        {
            fileInfo = default;
            var fd = (FileDesc)fileDesc0;
            if (fd.Stream is null)
                return STATUS_INVALID_DEVICE_REQUEST;
            if (_upperRoot is null)
                return STATUS_ACCESS_DENIED;

            CopyUp(fd);
            try
            {
                fd.Stream.SetLength(0);

                var path = fd.BackingPath;
                if (replaceFileAttributes)
                {
                    var attr = (System.IO.FileAttributes)fileAttributes;
                    if (attr == 0)
                        attr = System.IO.FileAttributes.Archive;
                    File.SetAttributes(path, attr);
                }
                else if (fileAttributes != 0)
                {
                    var current = File.GetAttributes(path);
                    File.SetAttributes(path, current | (System.IO.FileAttributes)fileAttributes
                                                   | System.IO.FileAttributes.Archive);
                }

                return fd.GetFileInfo(out fileInfo);
            }
            catch (Exception)
            {
                return STATUS_ACCESS_DENIED;
            }
        }

        public override Int32 SetBasicInfo(
            object fileNode, object fileDesc0,
            uint fileAttributes, ulong creationTime, ulong lastAccessTime,
            ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo)
        {
            fileInfo = default;
            var fd = (FileDesc)fileDesc0;
            if (_upperRoot is null)
                return STATUS_ACCESS_DENIED;

            CopyUp(fd);
            var path = fd.BackingPath;
            try
            {
                if (fileAttributes != unchecked((uint)(-1)) && fileAttributes != 0)
                {
                    var attr = (System.IO.FileAttributes)fileAttributes;
                    File.SetAttributes(path, attr);
                }

                if (creationTime != 0)
                    File.SetCreationTimeUtc(path, DateTime.FromFileTimeUtc((long)creationTime));
                if (lastAccessTime != 0)
                    File.SetLastAccessTimeUtc(path, DateTime.FromFileTimeUtc((long)lastAccessTime));
                if (lastWriteTime != 0)
                    File.SetLastWriteTimeUtc(path, DateTime.FromFileTimeUtc((long)lastWriteTime));

                return fd.GetFileInfo(out fileInfo);
            }
            catch (Exception)
            {
                return STATUS_ACCESS_DENIED;
            }
        }

        public override Int32 SetFileSize(
            object fileNode, object fileDesc0,
            ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
        {
            fileInfo = default;
            var fd = (FileDesc)fileDesc0;
            if (fd.Stream is null)
                return STATUS_INVALID_DEVICE_REQUEST;
            if (_upperRoot is null)
                return STATUS_ACCESS_DENIED;

            CopyUp(fd);
            try
            {
                // Allocation size and file size collapse to the same notion for a
                // plain file backed by NTFS: NTFS manages allocation internally, so
                // we honor EOF truncation/extension directly.
                if (setAllocationSize)
                {
                    // Only truncate; never extend allocation beyond EOF.
                    if (newSize < (ulong)fd.Stream.Length)
                        fd.Stream.SetLength((long)newSize);
                }
                else
                {
                    fd.Stream.SetLength((long)newSize);
                }

                return fd.GetFileInfo(out fileInfo);
            }
            catch (Exception)
            {
                return STATUS_ACCESS_DENIED;
            }
        }

        public override Int32 CanDelete(object fileNode, object fileDesc0, string fileName)
        {
            var fd = (FileDesc)fileDesc0;
            if (_upperRoot is null)
                return STATUS_ACCESS_DENIED;

            if (fd.IsDirectory)
            {
                // A directory may only be deleted if it is empty (in either layer,
                // but practically the upper shadow must be empty and not shadow a
                // non-empty lower dir). Defer the real delete to Cleanup.
                try
                {
                    if (Directory.EnumerateFileSystemEntries(fd.BackingPath).Any())
                        return STATUS_DIRECTORY_NOT_EMPTY;
                }
                catch
                {
                    return STATUS_ACCESS_DENIED;
                }
            }

            fd.DeleteOnCleanup = true;
            return STATUS_SUCCESS;
        }

        public override Int32 Rename(
            object fileNode, object fileDesc0,
            string fileName, string newFileName, bool replaceIfExists)
        {
            var fd = (FileDesc)fileDesc0;
            if (_upperRoot is null)
                return STATUS_ACCESS_DENIED;

            // Promote so the rename source is a real upper-layer path we own.
            CopyUp(fd);
            var dest = ResolveUpperTarget(newFileName);
            if (dest is null)
                return STATUS_OBJECT_NAME_INVALID;

            try
            {
                if (File.Exists(dest) || Directory.Exists(dest))
                {
                    if (!replaceIfExists)
                        return STATUS_OBJECT_NAME_COLLISION;
                    if (Directory.Exists(dest))
                        Directory.Delete(dest, recursive: true);
                    else
                        File.Delete(dest);
                }

                var src = fd.BackingPath;
                if (fd.IsDirectory)
                {
                    Directory.Move(src, dest);
                    fd.DirInfo = new DirectoryInfo(dest);
                }
                else
                {
                    var oldPos = fd.Stream?.Position ?? 0;
                    fd.Stream?.Dispose();
                    File.Move(src, dest);
                    fd.Stream = new FileStream(
                        dest,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.Read | FileShare.Write | FileShare.Delete,
                        4096,
                        FileOptions.None);
                    try { fd.Stream.Position = oldPos; } catch { }
                    fd.DirInfo = null;
                }

                fd.BackingPath = dest;
                fd.FileName = newFileName;
                return STATUS_SUCCESS;
            }
            catch (Exception)
            {
                return STATUS_ACCESS_DENIED;
            }
        }

        public override Int32 Flush(object fileNode, object fileDesc0, out FileInfo fileInfo)
        {
            fileInfo = default;
            var fd = fileDesc0 as FileDesc;
            try
            {
                fd?.Stream?.Flush();
                if (fd is not null)
                    return fd.GetFileInfo(out fileInfo);
                return STATUS_SUCCESS;
            }
            catch (Exception)
            {
                return STATUS_ACCESS_DENIED;
            }
        }

        public override void Cleanup(object fileNode, object fileDesc0, string fileName, uint flags)
        {
            var fd = (FileDesc)fileDesc0;

            // Flush buffered writes now: WinFsp sends Cleanup on the last user handle close,
            // and Close may be deferred, so persisted data must hit the backing file here.
            try
            {
                fd.Stream?.Flush();
            }
            catch
            {
                // best-effort
            }

            // Honor a pending delete (set by CanDelete). The kernel guarantees this
            // is the last outstanding cleanup for the file object.
            if ((flags & CleanupDelete) != 0 || fd.DeleteOnCleanup)
            {
                try
                {
                    fd.Stream?.Dispose();
                    fd.Stream = null;
                    if (fd.IsDirectory)
                    {
                        if (Directory.Exists(fd.BackingPath))
                            Directory.Delete(fd.BackingPath, recursive: false);
                    }
                    else
                    {
                        if (File.Exists(fd.BackingPath))
                            File.Delete(fd.BackingPath);
                    }
                }
                catch
                {
                    // Best-effort: deletion failures are not reportable from Cleanup.
                }
                fd.DeleteOnCleanup = false;
            }
        }
    }
}
