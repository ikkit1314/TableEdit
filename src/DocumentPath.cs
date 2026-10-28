using System.Runtime.InteropServices;

namespace TableEdit;

internal static class DocumentPath
{
    public static string Normalize(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsMacOS()) return fullPath;

        // Finder resolves symlinks such as /var -> /private/var. Use the same
        // physical path for command-line, picker and activation requests.
        IntPtr resolved = RealPath(fullPath, IntPtr.Zero);
        if (resolved == IntPtr.Zero) return fullPath; // Preserve normal file-open error reporting.
        try { return Marshal.PtrToStringUTF8(resolved) ?? fullPath; }
        finally { Free(resolved); }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}
