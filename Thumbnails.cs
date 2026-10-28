using Cascade.UI;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Transform;
using ImageFrame = SharpImage.Image.ImageFrame;

namespace Peepr;

/// <summary>
/// Small previews for the thumbnail strip. Photos are decoded and shrunk (Lanczos) one at a time on a background
/// thread, nearest to the current file first; the most recently used few hundred are kept, about 30 KB each.
/// Videos have no thumbnail (the strip shows a film glyph for them).
/// </summary>
internal sealed class ThumbnailCache
{
    public const int PixelSize = 144;
    private const int Capacity = 240;

    private readonly Dictionary<string, LinkedListNode<(string Path, ImageSource? Image)>> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Path, ImageSource? Image)> recency = new();
    private readonly List<string> wanted = [];
    private bool working;

    /// <summary>Raised on the UI thread when a thumbnail becomes available.</summary>
    public event Action? ThumbnailReady;

    /// <summary>The thumbnail for <paramref name="path"/> if it is ready; null while pending or for a video.</summary>
    public ImageSource? Get(string path)
    {
        if (entries.TryGetValue(path, out var node))
        {
            recency.Remove(node);
            recency.AddFirst(node);
            return node.Value.Image;
        }
        return null;
    }

    /// <summary>Asks for thumbnails of <paramref name="paths"/>, most important first; replaces any earlier request.</summary>
    public void Request(IEnumerable<string> paths)
    {
        wanted.Clear();
        foreach (string path in paths)
        {
            if (!entries.ContainsKey(path) && MediaTypes.KindOf(path) == MediaKind.Image)
            {
                wanted.Add(path);
            }
        }
        if (!working && wanted.Count > 0)
        {
            working = true;
            _ = WorkAsync();
        }
    }

    // One decode at a time, on the thread pool, resuming on the UI thread to store each result.
    private async Task WorkAsync()
    {
        try
        {
            while (wanted.Count > 0)
            {
                string path = wanted[0];
                wanted.RemoveAt(0);
                if (entries.ContainsKey(path))
                {
                    continue;
                }

                (byte[] Pixels, int Width, int Height)? thumbnail = await Task.Run(() => Decode(path));
                Store(path, thumbnail is { } t ? ImageSource.FromPixels(t.Pixels, t.Width, t.Height) : null);
                ThumbnailReady?.Invoke();
            }
        }
        finally
        {
            working = false;
        }
    }

    private void Store(string path, ImageSource? image)
    {
        var node = recency.AddFirst((path, image));
        entries[path] = node;
        while (recency.Count > Capacity)
        {
            var oldest = recency.Last!;
            recency.RemoveLast();
            entries.Remove(oldest.Value.Path);
            oldest.Value.Image?.Dispose();
        }
    }

    private static (byte[] Pixels, int Width, int Height)? Decode(string path)
    {
        try
        {
            using ImageFrame decoded = FormatRegistry.Read(path);
            using ImageFrame small = Resize.Thumbnail(decoded, PixelSize, PixelSize);
            if (PictureDecoder.NeedsOrienting(decoded))
            {
                small.Orientation = decoded.Orientation;
                using ImageFrame oriented = Geometry.AutoOrient(small);
                return (PictureDecoder.ToRgba(oriented), (int)oriented.Columns, (int)oriented.Rows);
            }
            return (PictureDecoder.ToRgba(small), (int)small.Columns, (int)small.Rows);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unreadable files simply get no preview.
            return null;
        }
    }
}
