using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace Peepr;

internal enum MediaKind : byte
{
    Image,
    Video,
}

internal enum SortOrder : byte
{
    Name,
    NewestFirst,
    OldestFirst,
    LargestFirst,
    Type,
}

/// <summary>The file types Peepr opens: images decode through SharpImage, videos play through MediaKernel.</summary>
internal static class MediaTypes
{
    public static readonly string[] ImageExtensions =
    [
        ".jpg", ".jpeg", ".jfif", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".ico",
        ".heic", ".heif", ".avif", ".jxl", ".tga", ".dds", ".psd", ".qoi", ".hdr", ".exr",
    ];

    public static readonly string[] VideoExtensions =
    [
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".asf", ".flv",
        ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp", ".ogv",
    ];

    private static readonly FrozenSet<string> imageSet = ImageExtensions.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> videoSet = VideoExtensions.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string path)
    {
        string extension = Path.GetExtension(path);
        return imageSet.Contains(extension) || videoSet.Contains(extension);
    }

    public static MediaKind KindOf(string path) =>
        videoSet.Contains(Path.GetExtension(path)) ? MediaKind.Video : MediaKind.Image;
}

/// <summary>
/// Orders file names the way Explorer does: runs of digits compare by value, so "img2" sorts before "img10".
/// </summary>
internal sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }
        if (left is null)
        {
            return -1;
        }
        if (right is null)
        {
            return 1;
        }

        ReadOnlySpan<char> a = left;
        ReadOnlySpan<char> b = right;
        int i = 0;
        int j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int digitsStartA = i;
                int digitsStartB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                {
                    i++;
                }
                while (j < b.Length && char.IsAsciiDigit(b[j]))
                {
                    j++;
                }

                int numberOrder = CompareDigitRuns(a[digitsStartA..i], b[digitsStartB..j]);
                if (numberOrder != 0)
                {
                    return numberOrder;
                }
                continue;
            }

            int charOrder = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (charOrder != 0)
            {
                return charOrder;
            }
            i++;
            j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    // Compares two digit runs by numeric value without parsing, so runs longer than a long still order correctly.
    private static int CompareDigitRuns(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');
        if (a.Length != b.Length)
        {
            return a.Length.CompareTo(b.Length);
        }
        return a.SequenceCompareTo(b);
    }
}

/// <summary>
/// The viewable files in one folder, in the user's sort order, and which one is on screen. Navigation wraps
/// around at both ends, as the original Peepr did.
/// </summary>
internal sealed class MediaFolder
{
    private readonly List<string> files;

    private MediaFolder(string directory, List<string> files, int currentIndex)
    {
        Directory = directory;
        this.files = files;
        CurrentIndex = currentIndex;
    }

    public string Directory { get; }

    public int CurrentIndex { get; private set; }

    public int Count => files.Count;

    public bool IsEmpty => files.Count == 0;

    public string? CurrentFile => files.Count == 0 ? null : files[CurrentIndex];

    public IReadOnlyList<string> Files => files;

    /// <summary>Scans the folder holding <paramref name="file"/> and positions on that file.</summary>
    public static MediaFolder Open(string file, SortOrder order)
    {
        string fullPath = Path.GetFullPath(file);
        string directory = Path.GetDirectoryName(fullPath) ?? fullPath;
        List<string> files = Scan(directory, order);

        int index = files.FindIndex(f => string.Equals(f, fullPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0 && File.Exists(fullPath))
        {
            // Opened by path even though its extension isn't one we list: still show it.
            files.Insert(0, fullPath);
            index = 0;
        }
        return new MediaFolder(directory, files, Math.Max(index, 0));
    }

    /// <summary>Opens a folder directly, positioned on its first file in sort order.</summary>
    public static MediaFolder OpenDirectory(string directory, SortOrder order) =>
        new(Path.GetFullPath(directory), Scan(Path.GetFullPath(directory), order), 0);

    public static List<string> Scan(string directory, SortOrder order)
    {
        var files = new List<string>();
        try
        {
            foreach (string path in System.IO.Directory.EnumerateFiles(directory))
            {
                if (MediaTypes.IsSupported(path))
                {
                    files.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder is just an empty one.
        }

        Sort(files, order);
        return files;
    }

    public static void Sort(List<string> files, SortOrder order)
    {
        switch (order)
        {
            case SortOrder.Name:
                files.Sort(CompareNames);
                break;
            case SortOrder.Type:
                files.Sort((a, b) =>
                {
                    int byType = StringComparer.OrdinalIgnoreCase.Compare(Path.GetExtension(a), Path.GetExtension(b));
                    return byType != 0 ? byType : CompareNames(a, b);
                });
                break;
            case SortOrder.NewestFirst:
            case SortOrder.OldestFirst:
            case SortOrder.LargestFirst:
                SortByFileInfo(files, order);
                break;
        }
    }

    // Reads each file's metadata once rather than on every comparison.
    private static void SortByFileInfo(List<string> files, SortOrder order)
    {
        var keys = new long[files.Count];
        string[] paths = files.ToArray();
        for (int i = 0; i < paths.Length; i++)
        {
            var info = new FileInfo(paths[i]);
            keys[i] = order == SortOrder.LargestFirst
                ? (info.Exists ? info.Length : 0)
                : info.LastWriteTimeUtc.Ticks;
        }

        bool descending = order != SortOrder.OldestFirst;
        int[] indices = Enumerable.Range(0, paths.Length).ToArray();
        Array.Sort(indices, (x, y) =>
        {
            int byKey = descending ? keys[y].CompareTo(keys[x]) : keys[x].CompareTo(keys[y]);
            return byKey != 0 ? byKey : CompareNames(paths[x], paths[y]);
        });

        files.Clear();
        foreach (int index in indices)
        {
            files.Add(paths[index]);
        }
    }

    private static int CompareNames(string a, string b) =>
        NaturalStringComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b));

    /// <summary>The file <paramref name="offset"/> steps from the current one, wrapping at the ends.</summary>
    public string? FileAt(int offset)
    {
        if (files.Count == 0)
        {
            return null;
        }
        return files[Wrap(CurrentIndex + offset)];
    }

    public void MoveBy(int offset)
    {
        if (files.Count > 0)
        {
            CurrentIndex = Wrap(CurrentIndex + offset);
        }
    }

    public void MoveTo(int index)
    {
        if (files.Count > 0)
        {
            CurrentIndex = Math.Clamp(index, 0, files.Count - 1);
        }
    }

    /// <summary>Drops the current file (deleted or vanished); the next one takes its place.</summary>
    public (string Path, int Index)? RemoveCurrent()
    {
        if (files.Count == 0)
        {
            return null;
        }

        int index = CurrentIndex;
        string path = files[index];
        files.RemoveAt(index);
        if (CurrentIndex >= files.Count)
        {
            CurrentIndex = Math.Max(files.Count - 1, 0);
        }
        return (path, index);
    }

    /// <summary>Puts a file back where it was (undo) and shows it.</summary>
    public void Restore(string path, int index)
    {
        index = Math.Clamp(index, 0, files.Count);
        files.Insert(index, path);
        CurrentIndex = index;
    }

    /// <summary>Re-sorts in place, keeping the same file on screen.</summary>
    public void Resort(SortOrder order)
    {
        string? current = CurrentFile;
        Sort(files, order);
        if (current is not null)
        {
            CurrentIndex = Math.Max(files.IndexOf(current), 0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Wrap(int index)
    {
        int count = files.Count;
        int wrapped = index % count;
        return wrapped < 0 ? wrapped + count : wrapped;
    }
}
