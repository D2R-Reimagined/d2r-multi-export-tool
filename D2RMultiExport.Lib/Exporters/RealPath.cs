// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// The location a file or directory <em>actually</em> has once every symbolic link and
/// junction is resolved, read from an open handle rather than from a path string. Used by
/// <see cref="ExportOutputs"/> to confirm that a freshly created temp file landed inside the
/// export root even if a link was planted after the path was validated.
/// <para>
/// Supported on Windows (<c>GetFinalPathNameByHandleW</c>) and Linux (<c>/proc/self/fd</c>).
/// Elsewhere the methods return <c>null</c> and the caller falls back to its path checks.
/// </para>
/// </summary>
internal static class RealPath
{
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>The resolved path of an open file handle, or <c>null</c> when unavailable.</summary>
    public static string? OfHandle(SafeFileHandle handle)
    {
        if (handle.IsInvalid) return null;
        if (OperatingSystem.IsWindows()) return WindowsFinalPath(handle);
        if (OperatingSystem.IsLinux())
        {
            var link = $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}";
            return new FileInfo(link).LinkTarget;
        }
        return null;
    }

    /// <summary>The resolved path of an existing directory, or <c>null</c> when unavailable.</summary>
    public static string? OfDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFileW(directory, 0, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            return handle.IsInvalid ? null : WindowsFinalPath(handle);
        }
        if (OperatingSystem.IsLinux())
        {
            // File.OpenHandle rejects directories. Linux open(2) accepts them, and
            // /proc/self/fd resolves the resulting handle through every parent link.
            var descriptor = OpenDirectory(directory, 0);
            if (descriptor < 0) return null;
            using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            return OfHandle(handle);
        }
        return null;
    }

    /// <summary>
    /// Resolves an existing directory, or its closest existing parent plus the uncreated
    /// suffix. Parent links are therefore accounted for before staging creates directories.
    /// Returns null on platforms without handle-based directory resolution.
    /// </summary>
    public static string? OfDirectoryOrParent(string path)
    {
        var parent = path;
        while (!Directory.Exists(parent))
        {
            var next = Path.GetDirectoryName(parent);
            if (next is null || next == parent) return null;
            parent = next;
        }
        var real = OfDirectory(parent);
        return real is null ? null : Path.GetFullPath(Path.Combine(real, Path.GetRelativePath(parent, path)));
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or below it.</summary>
    public static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, comparison) || string.Equals(path, root, comparison);
    }

    private static string? WindowsFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) return null;
        if (length > buffer.Capacity)
        {
            buffer = new StringBuilder((int)length);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) return null;
        }
        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(string path, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
