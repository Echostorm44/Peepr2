using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Channels;
using ImageSource = Cascade.UI.ImageSource;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Transform;

namespace Peepr;

/// <summary>
/// A picture decoded to RGBA8 (sRGB, straight alpha, tightly packed), ready to become a texture. An animated
/// GIF / WebP / AVIF carries its first frame here and the rest in <see cref="Animation"/>, composited as it plays.
/// </summary>
internal sealed class DecodedPicture
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>The still, or an animation's first frame.</summary>
    public required byte[] Pixels { get; init; }

    /// <summary>Null for a still. Frames are composited as they play, so memory holds a few of them, not all.</summary>
    public AnimationSource? Animation { get; init; }

    /// <summary>
    /// Successively halved copies of a large still (largest first, not including the full size), for drawing it
    /// small without aliasing — the GPU has no mipmaps for it. Empty for animations and pictures that already fit.
    /// </summary>
    public PictureLevel[] SmallerLevels { get; init; } = [];

    public bool IsAnimated => Animation is not null;
}

/// <summary>One RGBA8 copy of a picture at a reduced size.</summary>
internal readonly record struct PictureLevel(byte[] Pixels, int Width, int Height);

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
            && TryDecodeAnimation(path, data, format, out DecodedPicture? animation))
        {
            return animation;
        }

        using ImageFrame decoded = FormatRegistry.Decode(data, format);
        using ImageFrame? oriented = NeedsOrienting(decoded) ? Geometry.AutoOrient(decoded) : null;
        ImageFrame frame = oriented ?? decoded;
        int width = (int)frame.Columns;
        int height = (int)frame.Rows;
        byte[] pixels = ToRgba(frame);

        return new DecodedPicture
        {
            Width = width,
            Height = height,
            Pixels = pixels,
            SmallerLevels = BuildSmallerLevels(pixels, width, height),
        };
    }

    // Halving stops once a level fits in this on both sides: a screen-sized picture of a large photo.
    private const int SmallestLevelEdge = 2048;

    /// <summary>
    /// Halves the picture until it fits in <see cref="SmallestLevelEdge"/>. Each level is a 2×2 box average of the
    /// one above, so drawing any size takes at most a 2:1 bilinear step from the nearest larger level — what
    /// mipmapping does on a GPU.
    /// </summary>
    internal static PictureLevel[] BuildSmallerLevels(byte[] pixels, int width, int height)
    {
        var levels = new List<PictureLevel>();
        while (width > SmallestLevelEdge || height > SmallestLevelEdge)
        {
            (pixels, width, height) = HalveRgba(pixels, width, height);
            levels.Add(new PictureLevel(pixels, width, height));
            if (width == 1 && height == 1)
            {
                break;
            }
        }
        return [.. levels];
    }

    /// <summary>A half-size copy: each output pixel is the rounded mean of the 2×2 block it covers (edges clamp).</summary>
    internal static (byte[] Pixels, int Width, int Height) HalveRgba(byte[] source, int width, int height)
    {
        int halfWidth = Math.Max(1, width / 2);
        int halfHeight = Math.Max(1, height / 2);
        var half = GC.AllocateUninitializedArray<byte>(halfWidth * halfHeight * 4);
        int sourceStride = width * 4;

        for (int y = 0; y < halfHeight; y++)
        {
            int top = Math.Min(y * 2, height - 1) * sourceStride;
            int bottom = Math.Min(y * 2 + 1, height - 1) * sourceStride;
            ReadOnlySpan<byte> upper = source.AsSpan(top, sourceStride);
            ReadOnlySpan<byte> lower = source.AsSpan(bottom, sourceStride);
            Span<byte> output = half.AsSpan(y * halfWidth * 4, halfWidth * 4);

            for (int x = 0; x < halfWidth; x++)
            {
                int left = Math.Min(x * 2, width - 1) * 4;
                int right = Math.Min(x * 2 + 1, width - 1) * 4;
                int target = x * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    output[target + channel] = (byte)((upper[left + channel] + upper[right + channel]
                        + lower[left + channel] + lower[right + channel] + 2) >> 2);
                }
            }
        }
        return (half, halfWidth, halfHeight);
    }

    public static bool NeedsOrienting(ImageFrame frame) =>
        frame.Orientation is not (SharpImage.Core.OrientationType.Undefined or SharpImage.Core.OrientationType.TopLeft);

    // An animation keeps its file's bytes (GIF, read a frame at a time) or SharpImage's decoded sequence (WebP and
    // AVIF, which it only decodes whole), plus the first frame to show at once. A single frame is a still.
    private static bool TryDecodeAnimation(string path, byte[] data, ImageFileFormat format,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DecodedPicture? animation)
    {
        animation = null;
        AnimationSource source;
        try
        {
            source = format == ImageFileFormat.Gif
                ? AnimationSource.FromGif(data)
                : AnimationSource.FromSequence(FormatRegistry.ReadSequence(path));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not a sequence after all (a still WebP, say): the single-frame path decodes it.
            return false;
        }

        if (!source.IsAnimated)
        {
            return false;
        }

        var first = new byte[source.Width * source.Height * 4];
        source.Open().ReadNext(first);
        animation = new DecodedPicture { Width = source.Width, Height = source.Height, Pixels = first, Animation = source };
        return true;
    }

    internal static int FrameDuration(ImageFrame frame, long timescale)
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
    internal static void BlendOnto(ImageFrame frame, byte[] canvas, int canvasWidth, int canvasHeight)
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

    internal static void ClearRect(byte[] canvas, int canvasWidth, int canvasHeight, int left, int top, int width, int height)
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
/// The frames of an animation, decoded on demand: a GIF's file bytes (SharpImage reads them a frame at a time), or
/// a WebP / AVIF sequence SharpImage has decoded whole. Immutable, so the cache and a player can share it; each
/// <see cref="Open"/> starts an independent pass.
/// </summary>
internal sealed class AnimationSource
{
    private readonly byte[]? gif;
    private readonly ImageSequence? sequence;

    private AnimationSource(byte[]? gif, ImageSequence? sequence, int width, int height, bool isAnimated)
    {
        this.gif = gif;
        this.sequence = sequence;
        Width = width;
        Height = height;
        IsAnimated = isAnimated;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>False when there is only one frame: the file is a still that happens to use an animation format.</summary>
    public bool IsAnimated { get; }

    public static AnimationSource FromGif(byte[] data)
    {
        var reader = new GifFrameReader(new MemoryStream(data, writable: false));
        // Only the frame headers and data are walked here, not composited: a second frame makes it an animation.
        bool animated = false;
        using (ImageFrame? first = reader.ReadNextFrame())
        {
            if (first is null)
            {
                throw new InvalidDataException("GIF contains no image data.");
            }
            using ImageFrame? second = reader.ReadNextFrame();
            animated = second is not null;
        }
        return new AnimationSource(data, null, reader.CanvasWidth, reader.CanvasHeight, animated);
    }

    public static AnimationSource FromSequence(ImageSequence sequence)
    {
        int width = sequence.CanvasWidth > 0 ? sequence.CanvasWidth : (int)sequence.Frames[0].Columns;
        int height = sequence.CanvasHeight > 0 ? sequence.CanvasHeight : (int)sequence.Frames[0].Rows;
        return new AnimationSource(null, sequence, width, height, sequence.Count > 1);
    }

    public AnimationFrames Open() =>
        gif is not null
            ? new AnimationFrames(Width, Height, new GifFrameReader(new MemoryStream(gif, writable: false)), null)
            : new AnimationFrames(Width, Height, null, sequence);
}

/// <summary>
/// One pass over an animation: composites each frame onto the canvas in turn (honouring GIF disposal) and starts
/// again after the last. Holds one canvas, not the frames. Not thread-safe.
/// </summary>
internal sealed class AnimationFrames
{
    private readonly int width;
    private readonly int height;
    private readonly GifFrameReader? reader;
    private readonly ImageSequence? sequence;
    private byte[] canvas;
    private int sequenceIndex;

    public AnimationFrames(int width, int height, GifFrameReader? reader, ImageSequence? sequence)
    {
        this.width = width;
        this.height = height;
        this.reader = reader;
        this.sequence = sequence;
        canvas = new byte[width * height * 4];
    }

    /// <summary>Composites the next frame into <paramref name="output"/> (width × height × 4) and returns its duration.</summary>
    public int ReadNext(byte[] output)
    {
        ImageFrame frame = NextFrame();
        try
        {
            byte[]? beforeFrame = frame.DisposeMethod == SharpImage.Core.DisposeType.Previous ? (byte[])canvas.Clone() : null;
            PictureDecoder.BlendOnto(frame, canvas, width, height);
            canvas.CopyTo(output, 0);
            int duration = PictureDecoder.FrameDuration(frame, sequence?.Timescale ?? 0);

            if (frame.DisposeMethod == SharpImage.Core.DisposeType.Background)
            {
                PictureDecoder.ClearRect(canvas, width, height, frame.Page.X, frame.Page.Y, (int)frame.Columns, (int)frame.Rows);
            }
            else if (beforeFrame is not null)
            {
                canvas = beforeFrame;
            }
            return duration;
        }
        finally
        {
            // A GIF frame was decoded for this pass alone; a sequence's frames belong to the sequence.
            if (reader is not null)
            {
                frame.Dispose();
            }
        }
    }

    private ImageFrame NextFrame()
    {
        if (reader is not null)
        {
            if (reader.ReadNextFrame() is { } frame)
            {
                return frame;
            }
            reader.Rewind();
            Array.Clear(canvas);
            return reader.ReadNextFrame() ?? throw new InvalidDataException("GIF contains no image data.");
        }

        if (sequenceIndex == sequence!.Count)
        {
            sequenceIndex = 0;
            Array.Clear(canvas);
        }
        return sequence.Frames[sequenceIndex++];
    }
}

/// <summary>
/// Plays an <see cref="AnimationSource"/> into one <see cref="ImageSource"/>. A worker thread composites frames a
/// couple ahead into a few reused buffers; the UI thread calls <see cref="Advance"/> each display frame and writes
/// the frame that has fallen due into the texture in place, as video does. Memory is a few frames, whatever the
/// animation's length, and the first frame needs no wait.
/// </summary>
internal sealed class AnimationPlayer : IDisposable
{
    private const int FramesAhead = 2;

    private readonly ImageSource target;
    private readonly Channel<(byte[] Pixels, int DurationMs)> ready =
        Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(FramesAhead) { SingleReader = true, SingleWriter = true });
    private readonly ConcurrentBag<byte[]> spare = [];
    private float elapsedMs;
    // The frame on screen until the first composited one arrives is the decoded first frame: show the next at once.
    private int shownDurationMs;

    public AnimationPlayer(AnimationSource source, ImageSource target)
    {
        this.target = target;
        _ = Task.Run(() => CompositeAheadAsync(source));
    }

    /// <summary>UI thread, once per display frame: shows the next frame when the current one's time is up.</summary>
    public void Advance(float deltaMs)
    {
        elapsedMs += deltaMs;
        while (elapsedMs >= shownDurationMs && ready.Reader.TryRead(out (byte[] Pixels, int DurationMs) next))
        {
            elapsedMs -= shownDurationMs;
            target.Update(next.Pixels, target.Width, target.Height);
            spare.Add(next.Pixels);
            shownDurationMs = next.DurationMs;
        }
        // Compositing fell behind (a huge frame): hold this one rather than racing through the backlog after.
        elapsedMs = MathF.Min(elapsedMs, shownDurationMs);
    }

    // Runs until Dispose closes the channel, which also releases a write waiting for room.
    private async Task CompositeAheadAsync(AnimationSource source)
    {
        try
        {
            AnimationFrames frames = source.Open();
            int length = source.Width * source.Height * 4;
            while (true)
            {
                byte[] buffer = spare.TryTake(out byte[]? reused) ? reused : new byte[length];
                int duration = frames.ReadNext(buffer);
                await ready.Writer.WriteAsync((buffer, duration)).ConfigureAwait(false);
            }
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A damaged frame part-way through: the animation stops on the last good frame.
        }
    }

    public void Dispose() => ready.Writer.TryComplete();
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
            decode = Task.Run(() =>
            {
                try
                {
                    return PictureDecoder.Decode(path);
                }
                finally
                {
                    MemoryRelief.AfterDecode();
                }
            });
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
            MemoryRelief.AfterLargeRelease();
        }
    }

    public void Forget(string path) => decodes.Remove(path);
}

/// <summary>
/// Gives memory back promptly after Peepr drops something big. Decoded photos, a decode's working buffers and video
/// frame pools live on the large object heap, which is only swept by a full collection; on a machine with plenty of
/// RAM the runtime can leave hundreds of megabytes of them dead for minutes. Peepr knows the moments it lets go of
/// something big, so it asks for a full, compacting collection then: one, a moment after the last of a burst
/// (opening a file decodes it and both neighbours at once).
/// </summary>
internal static class MemoryRelief
{
    private const int SettleMs = 250;
    // Aggressive, because a background sweep frees the objects but leaves their pages committed to the process.
    // It blocks for a few milliseconds (25 at most for three 12-megapixel decodes), just after a file has opened.
    private static readonly Timer Sweep = new(_ => GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true));

    /// <summary>A decoded photo left the cache: sweep once things settle.</summary>
    public static void AfterLargeRelease() => Sweep.Change(SettleMs, Timeout.Infinite);

    /// <summary>
    /// A decode finished. SharpImage works in 16-bit buffers several times the size of the RGBA8 result, all
    /// garbage once it returns.
    /// </summary>
    public static void AfterDecode() => AfterLargeRelease();

    /// <summary>
    /// A video closed, dropping its decoder's frame pool (~100 MB at 1080p). A background sweep frees the
    /// objects but the runtime keeps the pages committed; an aggressive collection compacts and hands them back
    /// to Windows. It blocks for a few milliseconds, which nobody sees while the next file is opening.
    /// </summary>
    public static void AfterVideoClosed()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
