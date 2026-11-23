using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Grably.Platform.Shell;

/// <summary>Deletes files the way Explorer does: into the Recycle Bin, so the user can restore them.</summary>
public static class RecycleBin
{
    /// <summary>
    /// Moves the existing files among <paramref name="paths"/> to the Recycle Bin without asking. A file that cannot
    /// be recycled (network share, removable drive, too big for the bin) is never destroyed silently: Windows asks
    /// first, owned by <paramref name="owner"/>, and the user can keep it.
    /// </summary>
    /// <returns>False when Windows reported an error or the user kept some files; callers should check what is gone.</returns>
    public static unsafe bool Send(IEnumerable<string> paths, nint owner = 0)
    {
        var existing = paths.Where(File.Exists).Select(Path.GetFullPath).ToList();
        if (existing.Count == 0)
        {
            return true;
        }

        // pFrom is a list of null-terminated paths ending with an extra null.
        var from = string.Join('\0', existing) + "\0\0";
        fixed (char* fromPointer = from)
        {
            var operation = new SHFILEOPSTRUCTW
            {
                hwnd = new HWND(owner),
                wFunc = PInvoke.FO_DELETE,
                pFrom = fromPointer,
                fFlags = (ushort)(FILEOPERATION_FLAGS.FOF_ALLOWUNDO | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION
                    | FILEOPERATION_FLAGS.FOF_WANTNUKEWARNING | FILEOPERATION_FLAGS.FOF_SILENT | FILEOPERATION_FLAGS.FOF_NOERRORUI),
            };
            return PInvoke.SHFileOperation(ref operation) == 0 && !operation.fAnyOperationsAborted;
        }
    }
}
