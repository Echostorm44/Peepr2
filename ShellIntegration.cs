using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Peepr;

/// <summary>
/// Sends files to the Windows Recycle Bin and brings them back. Restoring reads the bin's own index files: for
/// every recycled item Windows writes a "$I" record holding the original path beside the "$R" data file, and
/// putting the "$R" back under that path is what Explorer's Restore does.
/// </summary>
internal static partial class RecycleBin
{
    private const uint FileOperationDelete = 0x0003;
    private const ushort AllowUndo = 0x0040;
    private const ushort NoConfirmation = 0x0010;
    private const ushort Silent = 0x0004;
    private const ushort NoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ShellFileOperation
    {
        public nint WindowHandle;
        public uint Function;
        public char* From;
        public char* To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public char* ProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static unsafe partial int SHFileOperation(ShellFileOperation* operation);

    /// <summary>Moves <paramref name="path"/> to the Recycle Bin. False if Windows refused (in use, no bin on the drive).</summary>
    public static unsafe bool Send(string path)
    {
        // pFrom is a list of paths ending in an empty one: the path, then two terminators.
        string from = path + "\0\0";
        fixed (char* fromPointer = from)
        {
            var operation = new ShellFileOperation
            {
                Function = FileOperationDelete,
                From = fromPointer,
                Flags = (ushort)(AllowUndo | NoConfirmation | Silent | NoErrorUi),
            };
            int result = SHFileOperation(&operation);
            return result == 0 && operation.AnyOperationsAborted == 0 && !File.Exists(path);
        }
    }

    /// <summary>
    /// Puts the most recently recycled copy of <paramref name="originalPath"/> back where it was. False when it is no
    /// longer in the bin (emptied, or restored already) or something now occupies that path.
    /// </summary>
    public static bool TryRestore(string originalPath)
    {
        if (File.Exists(originalPath) || Path.GetPathRoot(originalPath) is not { Length: > 0 } root)
        {
            return false;
        }

        string? newestIndex = null;
        long newestDeletion = long.MinValue;
        foreach (string indexFile in EnumerateIndexFiles(Path.Combine(root, "$Recycle.Bin")))
        {
            if (TryReadIndex(indexFile, out string recycledFrom, out long deletedAt)
                && deletedAt > newestDeletion
                && string.Equals(recycledFrom, originalPath, StringComparison.OrdinalIgnoreCase))
            {
                newestIndex = indexFile;
                newestDeletion = deletedAt;
            }
        }

        if (newestIndex is null)
        {
            return false;
        }

        string folder = Path.GetDirectoryName(newestIndex)!;
        string dataFile = Path.Combine(folder, "$R" + Path.GetFileName(newestIndex)[2..]);
        try
        {
            File.Move(dataFile, originalPath);
            File.Delete(newestIndex);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The bin holds one folder per user SID; other users' folders refuse access, which leaves just ours.
    private static IEnumerable<string> EnumerateIndexFiles(string binRoot)
    {
        string[] userFolders;
        try
        {
            userFolders = Directory.GetDirectories(binRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string userFolder in userFolders)
        {
            string[] indexFiles;
            try
            {
                indexFiles = Directory.GetFiles(userFolder, "$I*");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string indexFile in indexFiles)
            {
                yield return indexFile;
            }
        }
    }

    // $I layout: int64 version, int64 size, int64 FILETIME deleted, then the path. Version 2 (Windows 10+) stores an
    // int32 length then that many UTF-16 chars; version 1 stores a fixed 260-char buffer.
    internal static bool TryReadIndex(string indexFile, out string originalPath, out long deletedAt)
    {
        originalPath = "";
        deletedAt = 0;
        byte[] data;
        try
        {
            data = File.ReadAllBytes(indexFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return TryParseIndex(data, out originalPath, out deletedAt);
    }

    internal static bool TryParseIndex(ReadOnlySpan<byte> data, out string originalPath, out long deletedAt)
    {
        originalPath = "";
        deletedAt = 0;
        if (data.Length < 24)
        {
            return false;
        }

        long version = BinaryPrimitives.ReadInt64LittleEndian(data);
        deletedAt = BinaryPrimitives.ReadInt64LittleEndian(data[16..]);
        ReadOnlySpan<byte> pathBytes;
        if (version == 2 && data.Length >= 28)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(data[24..]);
            if (length <= 0 || 28 + (length * 2L) > data.Length)
            {
                return false;
            }
            pathBytes = data.Slice(28, length * 2);
        }
        else if (version == 1)
        {
            pathBytes = data[24..];
        }
        else
        {
            return false;
        }

        ReadOnlySpan<char> path = MemoryMarshal.Cast<byte, char>(pathBytes);
        int terminator = path.IndexOf('\0');
        originalPath = new string(terminator >= 0 ? path[..terminator] : path);
        return originalPath.Length > 0;
    }
}
