using System.Runtime.InteropServices;
using System.Text;

namespace EasyHomeServer.Modules.Disks.MergerFs;

/// <summary>
/// Reads extended attributes, via libc.
/// </summary>
/// <remarks>
/// <para>
/// A syscall rather than shelling out to <c>getfattr</c>. The rest of this module runs external
/// tools through <see cref="Sdk.ISystemRunner"/>, but those tools do real work that needs
/// privilege; this is one read, on a poll, once per key per pool. Spawning a process for it would
/// cost more than the read, and <c>getfattr</c> lives in <c>attr</c>, a package the module would
/// otherwise never need.
/// </para>
/// <para>
/// Linux only. Every caller is already reading a mergerfs mount, which cannot exist elsewhere.
/// </para>
/// </remarks>
internal static partial class Xattr
{
    /// <summary>Enough for any value mergerfs returns; a long branch list is the largest.</summary>
    private const int InitialBufferSize = 4096;

    /// <summary>Refuses to grow past this, so a bad value cannot exhaust memory.</summary>
    private const int MaximumBufferSize = 1 << 20;

    private const int ERANGE = 34;

    [LibraryImport("libc", EntryPoint = "getxattr", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetXattr(string path, string name, byte[]? value, nuint size);

    /// <summary>
    /// Reads one extended attribute as a string, or null when it is absent or unreadable.
    /// </summary>
    /// <remarks>
    /// Null rather than throwing: an older mergerfs simply does not answer for a key it does not
    /// know, and a caller reading five keys should not have to tell that apart from a real fault.
    /// </remarks>
    public static string? TryGet(string path, string name)
    {
        var size = InitialBufferSize;

        while (true)
        {
            var buffer = new byte[size];
            var length = GetXattr(path, name, buffer, (nuint)buffer.Length);

            if (length >= 0)
            {
                return Encoding.UTF8.GetString(buffer, 0, (int)length);
            }

            // ERANGE alone means the value outgrew the buffer; anything else is a real absence.
            if (Marshal.GetLastPInvokeError() != ERANGE || size >= MaximumBufferSize)
            {
                return null;
            }

            size *= 2;
        }
    }
}
