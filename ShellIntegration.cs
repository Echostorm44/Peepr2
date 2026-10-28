using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Peepr;

/// <summary>
/// Registers Peepr with Windows for its photo and video types, per user (no admin). Peepr joins each type's
/// "Open with" list and registers its capabilities, which is what puts it in Settings → Default apps. It does
/// not take over the default itself: Windows protects the user's choice, so "make Peepr the default" opens
/// Peepr's page in Default apps, where the user confirms it. Unregistering removes exactly what this wrote.
/// </summary>
internal static partial class FileAssociations
{
    public const string ImageProgId = "Peepr.Image";
    public const string VideoProgId = "Peepr.Video";

    private const string AppName = "Peepr";
    private const string CapabilitiesPath = @"Software\Peepr\Capabilities";
    private const string RegisteredApplicationsPath = @"Software\RegisteredApplications";
    private const string ApplicationsPath = @"Software\Classes\Applications\Peepr.exe";
    private const string ClassesPath = @"Software\Classes";

    public enum Status
    {
        NotRegistered,
        Registered,
        /// <summary>Registered, but for a Peepr.exe somewhere else (the app was moved or another copy registered).</summary>
        RegisteredElsewhere,
    }

    /// <summary>The running Peepr.exe: what Settings registers.</summary>
    public static string CurrentExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Peepr.exe");

    private static string OpenCommand(string exePath) => $"\"{exePath}\" \"%1\"";

    public static Status GetStatus(string exePath)
    {
        using RegistryKey? command = Registry.CurrentUser.OpenSubKey($@"{ClassesPath}\{ImageProgId}\shell\open\command");
        if (command?.GetValue(null) is not string registered)
        {
            return Status.NotRegistered;
        }
        return string.Equals(registered, OpenCommand(exePath), StringComparison.OrdinalIgnoreCase)
            ? Status.Registered
            : Status.RegisteredElsewhere;
    }

    /// <summary>Registers <paramref name="exePath"/> (the running app from Settings, the installed one from setup).</summary>
    public static void Register(string exePath)
    {
        string icon = $"\"{exePath}\",0";
        WriteProgId(ImageProgId, "Photo (Peepr)", icon, exePath);
        WriteProgId(VideoProgId, "Video (Peepr)", icon, exePath);

        using (RegistryKey application = Registry.CurrentUser.CreateSubKey(ApplicationsPath))
        {
            application.SetValue("FriendlyAppName", AppName);
            using RegistryKey command = application.CreateSubKey(@"shell\open\command");
            command.SetValue(null, OpenCommand(exePath));
            using RegistryKey supported = application.CreateSubKey("SupportedTypes");
            foreach (string extension in AllExtensions())
            {
                supported.SetValue(extension, "");
            }
        }

        using (RegistryKey capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "A fast, simple viewer for photos and videos.");
            capabilities.SetValue("ApplicationIcon", icon);
            using RegistryKey associations = capabilities.CreateSubKey("FileAssociations");
            foreach (string extension in MediaTypes.ImageExtensions)
            {
                associations.SetValue(extension, ImageProgId);
                AddToOpenWith(extension, ImageProgId);
            }
            foreach (string extension in MediaTypes.VideoExtensions)
            {
                associations.SetValue(extension, VideoProgId);
                AddToOpenWith(extension, VideoProgId);
            }
        }

        using (RegistryKey registered = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsPath))
        {
            registered.SetValue(AppName, CapabilitiesPath);
        }

        RemovePeeprOneRegistration();
        NotifyShell();
    }

    /// <summary>
    /// Peepr 1 registered one ProgID per extension ("Peepr.AssocFile.JPG"), wrote it as each extension's default
    /// and added a "peepr:" URL protocol. Left behind they show a second, possibly dead "Peepr" in Open with.
    /// </summary>
    private static void RemovePeeprOneRegistration()
    {
        foreach (string extension in AllExtensions())
        {
            string legacyProgId = "Peepr.AssocFile." + extension.TrimStart('.').ToUpperInvariant();
            RemoveFromOpenWith(extension, legacyProgId);
            using (RegistryKey? extensionKey = Registry.CurrentUser.OpenSubKey($@"{ClassesPath}\{extension}", writable: true))
            {
                if (extensionKey?.GetValue(null) is string current && current == legacyProgId)
                {
                    extensionKey.DeleteValue("", throwOnMissingValue: false);
                }
            }
            Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesPath}\{legacyProgId}", throwOnMissingSubKey: false);
        }

        bool hasPeeprOneProtocol;
        using (RegistryKey? protocol = Registry.CurrentUser.OpenSubKey($@"{ClassesPath}\peepr"))
        {
            hasPeeprOneProtocol = protocol?.GetValue("URL Protocol") is not null;
        }
        if (hasPeeprOneProtocol)
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesPath}\peepr", throwOnMissingSubKey: false);
        }
    }

    public static void Unregister()
    {
        foreach (string extension in MediaTypes.ImageExtensions)
        {
            RemoveFromOpenWith(extension, ImageProgId);
        }
        foreach (string extension in MediaTypes.VideoExtensions)
        {
            RemoveFromOpenWith(extension, VideoProgId);
        }

        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesPath}\{ImageProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesPath}\{VideoProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ApplicationsPath, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Peepr", throwOnMissingSubKey: false);
        using (RegistryKey? registered = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsPath, writable: true))
        {
            registered?.DeleteValue(AppName, throwOnMissingValue: false);
        }

        RemovePeeprOneRegistration();
        NotifyShell();
    }

    /// <summary>The HKCU keys <see cref="Register"/> creates and owns outright.</summary>
    public static IEnumerable<string> OwnedKeys() =>
    [
        $@"{ClassesPath}\{ImageProgId}",
        $@"{ClassesPath}\{VideoProgId}",
        ApplicationsPath,
        @"Software\Peepr",
    ];

    /// <summary>The single values <see cref="Register"/> writes into keys other apps share: (key, value name).</summary>
    public static IEnumerable<(string Key, string Name)> SharedValues()
    {
        foreach (string extension in MediaTypes.ImageExtensions)
        {
            yield return ($@"{ClassesPath}\{extension}\OpenWithProgids", ImageProgId);
        }
        foreach (string extension in MediaTypes.VideoExtensions)
        {
            yield return ($@"{ClassesPath}\{extension}\OpenWithProgids", VideoProgId);
        }
        yield return (RegisteredApplicationsPath, AppName);
    }

    /// <summary>Opens Peepr's page in Settings → Default apps, where the user can make it the default for everything at once.</summary>
    public static void OpenDefaultAppsSettings() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={AppName}")
        {
            UseShellExecute = true,
        });

    private static IEnumerable<string> AllExtensions() => MediaTypes.ImageExtensions.Concat(MediaTypes.VideoExtensions);

    private static void WriteProgId(string progId, string typeName, string icon, string exePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{ClassesPath}\{progId}");
        key.SetValue(null, typeName);
        using (RegistryKey defaultIcon = key.CreateSubKey("DefaultIcon"))
        {
            defaultIcon.SetValue(null, icon);
        }
        using RegistryKey open = key.CreateSubKey(@"shell\open");
        open.SetValue("FriendlyAppName", AppName);
        using RegistryKey command = open.CreateSubKey("command");
        command.SetValue(null, OpenCommand(exePath));
    }

    private static void AddToOpenWith(string extension, string progId)
    {
        using RegistryKey openWith = Registry.CurrentUser.CreateSubKey($@"{ClassesPath}\{extension}\OpenWithProgids");
        openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    private static void RemoveFromOpenWith(string extension, string progId)
    {
        using RegistryKey? openWith = Registry.CurrentUser.OpenSubKey($@"{ClassesPath}\{extension}\OpenWithProgids", writable: true);
        openWith?.DeleteValue(progId, throwOnMissingValue: false);
    }

    private const int ShellChangeAssociations = 0x08000000;

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    // Tells Explorer the associations changed so "Open with" and icons refresh without a sign-out.
    private static void NotifyShell() => SHChangeNotify(ShellChangeAssociations, 0, 0, 0);
}

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
