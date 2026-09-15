using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KiCad.Automation.Native;

/// <summary>Replace the target while retaining the file actually displaced by
/// the operation. Never fall back to check-then-overwrite or delete the backup.</summary>
internal static class PreservingFileReplacement
{
    internal static string PreviousPath(string staged) => OperatingSystem.IsWindows() ? staged + ".previous" : staged;

    internal static void Replace(string target, string staged)
    {
        int error;
        if (OperatingSystem.IsWindows())
        {
            // Flags 0 preserves metadata/ACL checks. A non-null backup is
            // essential: ReplaceFileW may partially rename files on failure.
            if (ReplaceFile(target, staged, PreviousPath(staged), 0, IntPtr.Zero, IntPtr.Zero)) return;
            error = Marshal.GetLastPInvokeError();
        }
        else if (OperatingSystem.IsLinux())
        {
            // Absolute paths: dirfd is ignored. RENAME_EXCHANGE = 2.
            if (RenameAt2(-100, staged, -100, target, 2) == 0) return;
            error = Marshal.GetLastPInvokeError();
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Apple sys/stdio.h: RENAME_SWAP = 0x00000002.
            if (RenameSwap(staged, target, 2) == 0) return;
            error = Marshal.GetLastPInvokeError();
        }
        else throw new PlatformNotSupportedException("Preserving file replacement is not supported on this platform.");
        throw new IOException($"Preserving file replacement failed ({error}): {new Win32Exception(error).Message}");
    }

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameSwap([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "ReplaceFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplaceFile(string target, string staged, string backup, uint flags,
        IntPtr exclude, IntPtr reserved);
}
