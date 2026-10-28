using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Transform;

namespace Peepr;

/// <summary>
/// A picture decoded to RGBA8 (sRGB, straight alpha, tightly packed), ready to become a texture. An animated
/// GIF / WebP / AVIF carries one fully composited buffer per frame.
/// </summary>
internal sealed class DecodedPicture
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[][] Frames { get; init; }
    public required int[] FrameDurationsMs { get; init; }

    public bool IsAnimated => Frames.Length > 1;
}

/// <summary>Turns image files into <see cref="DecodedPicture"/>s through SharpImage. Thread-safe; runs off the UI thread.</summary>
internal static class PictureDecoder
{
    // Browsers treat GIF delays of 0-1 centiseconds as "unset" and use 100 ms; match them or such GIFs race.
    private const int DefaultFrameDurationMs = 100;
    private const int MinimumFrameDurationMs = 20;

    public static DecodedPicture Decode(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        ImageFileFormat format = FormatRegistry.DetectFormat(data);
        if (format == ImageFileFormat.Unknown)
        {
            format = FormatRegistry.DetectFromExtension(path);
        }
        if (format == ImageFileFormat.Unknown)
        {
            throw new NotSupportedException("This isn't an image format Peepr recognises.");
        }

        if (format is ImageFileFormat.Gif or ImageFileFormat.WebP or ImageFileFormat.Avif
            && TryDecodeAnimation(path, out DecodedPicture? animation))
        {
            return animation;
        }

        using ImageFrame decoded = FormatRegistry.Decode(data, format);
        using ImageFrame? oriented = NeedsOrienting(decoded) ? Geometry.AutoOrient(decoded) : null;
        ImageFrame frame = oriented ?? decoded;

        return new DecodedPicture
        {
            Width = (int)frame.Columns,
            Height = (int)frame.Rows,
            Frames = [ToRgba(frame)],
            FrameDurationsMs = [0],
        };
    }

    private static bool NeedsOrienting(ImageFrame frame) =>
        frame.Orientation is not (SharpImage.Core.OrientationType.Undefined or SharpImage.Core.OrientationType.TopLeft);

    private static bool TryDecodeAnimation(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DecodedPicture? animation)
    {
        animation = null;
        ImageSequence sequence;
        try
        {
            sequence = FormatRegistry.ReadSequence(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not a sequence after all (a still WebP, say): the single-frame path decodes it.
            return false;
        }

        using (sequence)
        {
            if (sequence.Count < 2)
            {
                return false;
            }

            int width = sequence.CanvasWidth > 0 ? sequence.CanvasWidth : (int)sequence.Frames[0].Columns;
            int height = sequence.CanvasHeight > 0 ? sequence.CanvasHeight : (int)sequence.Frames[0].Rows;
            var frames = new byte[sequence.Count][];
            var durations = new int[sequence.Count];
            var canvas = new byte[width * height * 4];

            for (int i = 0; i < sequence.Count; i++)
            {
                ImageFrame frame = sequence.Frames[i];
                byte[]? beforeFrame = frame.DisposeMethod == SharpImage.Core.DisposeType.Previous ? (byte[])canvas.Clone() : null;

                BlendOnto(frame, canvas, width, height);
                frames[i] = (byte[])canvas.Clone();
                durations[i] = FrameDuration(frame, sequence.Timescale);

                if (frame.DisposeMethod == SharpImage.Core.DisposeType.Background)
                {
                    ClearRect(canvas, width, height, frame.Page.X, frame.Page.Y, (int)frame.Columns, (int)frame.Rows);
                }
                else if (beforeFrame is not null)
                {
                    canvas = beforeFrame;
                }
            }

            animation = new DecodedPicture { Width = width, Height = height, Frames = frames, FrameDurationsMs = durations };
            return true;
        }
    }

    private static int FrameDuration(ImageFrame frame, long timescale)
    {
        int milliseconds = timescale > 0 && frame.DurationTicks > 0
            ? (int)(frame.DurationTicks * 1000 / timescale)
            : frame.Delay * 10;
        return milliseconds < MinimumFrameDurationMs ? DefaultFrameDurationMs : milliseconds;
    }

    /// <summary>Converts a SharpImage frame (16-bit channels) to tightly packed RGBA8.</summary>
    public static byte[] ToRgba(ImageFrame frame)
    {
        int width = (int)frame.Columns;
        int height = (int)frame.Rows;
        var rgba = GC.AllocateUninitializedArray<byte>(width * height * 4);
        int channels = frame.NumberOfChannels;
        bool hasAlpha = frame.HasAlpha;

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<ushort> row = frame.GetPixelRow(y);
            Span<byte> destination = rgba.AsSpan(y * width * 4, width * 4);
            if (channels == 4 && hasAlpha)
            {
                NarrowRgbaRow(row[..(width * 4)], destination);
            }
            else
            {
                ExpandRow(row, destination, width, channels, hasAlpha);
            }
        }
        return rgba;
    }

    // RGBA16 → RGBA8 by keeping each channel's high byte: exact for 8-bit sources (stored as b * 257), and within
    // one level of SharpImage's rounded scale for true 16-bit ones, which no display can show anyway.
    private static void NarrowRgbaRow(ReadOnlySpan<ushort> source, Span<byte> destination)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref ushort sourceStart = ref MemoryMarshal.GetReference(source);
            ref byte destinationStart = ref MemoryMarshal.GetReference(destination);
            int vectorEnd = source.Length - 16;
            for (; i <= vectorEnd; i += 16)
            {
                Vector128<ushort> low = Vector128.ShiftRightLogical(Vector128.LoadUnsafe(ref sourceStart, (nuint)i), 8);
                Vector128<ushort> high = Vector128.ShiftRightLogical(Vector128.LoadUnsafe(ref sourceStart, (nuint)(i + 8)), 8);
                Vector128.Narrow(low, high).StoreUnsafe(ref destinationStart, (nuint)i);
            }
        }
        for (; i < source.Length; i++)
        {
            destination[i] = (byte)(source[i] >> 8);
        }
    }

    private static void ExpandRow(ReadOnlySpan<ushort> row, Span<byte> destination, int width, int channels, bool hasAlpha)
    {
        for (int x = 0; x < width; x++)
        {
            int source = x * channels;
            byte r = (byte)(row[source] >> 8);
            byte g = channels > 2 ? (byte)(row[source + 1] >> 8) : r;
            byte b = channels > 2 ? (byte)(row[source + 2] >> 8) : r;
            byte a = hasAlpha ? (byte)(row[source + channels - 1] >> 8) : (byte)255;

            int target = x * 4;
            destination[target] = r;
            destination[target + 1] = g;
            destination[target + 2] = b;
            destination[target + 3] = a;
        }
    }

    // Draws one animation frame over the canvas at its page offset (alpha "over"), clipped to the canvas.
    private static void BlendOnto(ImageFrame frame, byte[] canvas, int canvasWidth, int canvasHeight)
    {
        int frameWidth = (int)frame.Columns;
        int frameHeight = (int)frame.Rows;
        int channels = frame.NumberOfChannels;
        bool hasAlpha = frame.HasAlpha;
        byte[] rowBuffer = new byte[frameWidth * 4];

        for (int y = 0; y < frameHeight; y++)
        {
            int canvasY = frame.Page.Y + y;
            if ((uint)canvasY >= (uint)canvasHeight)
            {
                continue;
            }

            ExpandRow(frame.GetPixelRow(y), rowBuffer, frameWidth, channels, hasAlpha);
            for (int x = 0; x < frameWidth; x++)
            {
                int canvasX = frame.Page.X + x;
                if ((uint)canvasX >= (uint)canvasWidth)
                {
                    continue;
                }

                int source = x * 4;
                int alpha = rowBuffer[source + 3];
                if (alpha == 0)
                {
                    continue;
                }

                int target = ((canvasY * canvasWidth) + canvasX) * 4;
                if (alpha == 255)
                {
                    canvas[target] = rowBuffer[source];
                    canvas[target + 1] = rowBuffer[source + 1];
                    canvas[target + 2] = rowBuffer[source + 2];
                    canvas[target + 3] = 255;
                    continue;
                }

                int inverse = 255 - alpha;
                int baseAlpha = canvas[target + 3];
                canvas[target] = (byte)(((rowBuffer[source] * alpha) + (canvas[target] * inverse) + 127) / 255);
                canvas[target + 1] = (byte)(((rowBuffer[source + 1] * alpha) + (canvas[target + 1] * inverse) + 127) / 255);
                canvas[target + 2] = (byte)(((rowBuffer[source + 2] * alpha) + (canvas[target + 2] * inverse) + 127) / 255);
                canvas[target + 3] = (byte)(alpha + ((baseAlpha * inverse) + 127) / 255);
            }
        }
    }

    private static void ClearRect(byte[] canvas, int canvasWidth, int canvasHeight, int left, int top, int width, int height)
    {
        int startX = Math.Clamp(left, 0, canvasWidth);
        int endX = Math.Clamp(left + width, 0, canvasWidth);
        int startY = Math.Clamp(top, 0, canvasHeight);
        int endY = Math.Clamp(top + height, 0, canvasHeight);
        for (int y = startY; y < endY; y++)
        {
            canvas.AsSpan(((y * canvasWidth) + startX) * 4, (endX - startX) * 4).Clear();
        }
    }
}

/// <summary>
/// Decodes pictures in the background and keeps the ones around the current file, so stepping forward or back
/// shows the next picture at once instead of waiting on a decode.
/// </summary>
internal sealed class PictureCache
{
    private readonly Dictionary<string, Task<DecodedPicture>> decodes = new(StringComparer.OrdinalIgnoreCase);

    public Task<DecodedPicture> Get(string path)
    {
        if (!decodes.TryGetValue(path, out Task<DecodedPicture>? decode))
        {
            decode = Task.Run(() => PictureDecoder.Decode(path));
            decodes[path] = decode;
        }
        return decode;
    }

    /// <summary>Starts decoding a picture we expect to need soon. Videos are ignored.</summary>
    public void Prefetch(string? path)
    {
        if (path is not null && MediaTypes.KindOf(path) == MediaKind.Image)
        {
            _ = Get(path);
        }
    }

    /// <summary>Forgets every decode except <paramref name="keep"/>, so memory holds a few pictures, not a folder.</summary>
    public void RetainOnly(ReadOnlySpan<string?> keep)
    {
        List<string>? stale = null;
        foreach (string path in decodes.Keys)
        {
            bool wanted = false;
            foreach (string? kept in keep)
            {
                if (kept is not null && string.Equals(kept, path, StringComparison.OrdinalIgnoreCase))
                {
                    wanted = true;
                    break;
                }
            }
            if (!wanted)
            {
                (stale ??= []).Add(path);
            }
        }

        if (stale is not null)
        {
            foreach (string path in stale)
            {
                decodes.Remove(path);
            }
        }
    }

    public void Forget(string path) => decodes.Remove(path);
}
