using System.Buffers.Binary;
using System.Text;
using SharpImage.Image;

namespace Peepr.Tests;

internal sealed class RecycleBinIndexTests
{
    private static byte[] VersionTwoIndex(string path, long deletedAt)
    {
        string terminated = path + "\0";
        var data = new byte[28 + terminated.Length * 2];
        BinaryPrimitives.WriteInt64LittleEndian(data, 2);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), 12345);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), deletedAt);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), terminated.Length);
        Encoding.Unicode.GetBytes(terminated).CopyTo(data, 28);
        return data;
    }

    [Test]
    public async Task VersionTwo_ReadsPathAndDeletionTime()
    {
        byte[] data = VersionTwoIndex(@"D:\Photos\Holiday 2026\beach.jpg", 133_000_000_000_000_000);

        bool parsed = RecycleBin.TryParseIndex(data, out string path, out long deletedAt);

        await Assert.That(parsed).IsTrue();
        await Assert.That(path).IsEqualTo(@"D:\Photos\Holiday 2026\beach.jpg");
        await Assert.That(deletedAt).IsEqualTo(133_000_000_000_000_000);
    }

    [Test]
    public async Task VersionOne_ReadsTheFixedLengthPath()
    {
        var data = new byte[24 + 520];
        BinaryPrimitives.WriteInt64LittleEndian(data, 1);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), 42);
        Encoding.Unicode.GetBytes(@"C:\old\clip.mp4").CopyTo(data, 24);

        bool parsed = RecycleBin.TryParseIndex(data, out string path, out long deletedAt);

        await Assert.That(parsed).IsTrue();
        await Assert.That(path).IsEqualTo(@"C:\old\clip.mp4");
        await Assert.That(deletedAt).IsEqualTo(42);
    }

    [Test]
    public async Task TruncatedOrUnknown_IsRejected()
    {
        byte[] valid = VersionTwoIndex(@"C:\a.jpg", 1);
        byte[] unknownVersion = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(unknownVersion, 9);

        await Assert.That(RecycleBin.TryParseIndex(valid.AsSpan(0, 30), out _, out _)).IsFalse();
        await Assert.That(RecycleBin.TryParseIndex(unknownVersion, out _, out _)).IsFalse();
        await Assert.That(RecycleBin.TryParseIndex(new byte[10], out _, out _)).IsFalse();
    }
}

internal sealed class PictureDecoderTests
{
    [Test]
    public async Task ToRgba_KeepsTheHighByteOfEachChannel()
    {
        // 5 pixels wide so the SIMD loop (16 channels at a time) and the scalar tail both run.
        using var frame = new ImageFrame();
        frame.Initialize(5, 1, SharpImage.Core.ColorspaceType.SRGB, hasAlpha: true);
        Span<ushort> row = frame.GetPixelRowForWrite(0);
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = (ushort)((i * 13 % 256) * 257);
        }

        byte[] rgba = PictureDecoder.ToRgba(frame);

        await Assert.That(rgba.Length).IsEqualTo(20);
        for (int i = 0; i < rgba.Length; i++)
        {
            await Assert.That((int)rgba[i]).IsEqualTo(i * 13 % 256);
        }
    }

    [Test]
    public async Task ToRgba_ExpandsOpaqueRgb()
    {
        using var frame = new ImageFrame();
        frame.Initialize(2, 1, SharpImage.Core.ColorspaceType.SRGB, hasAlpha: false);
        Span<ushort> row = frame.GetPixelRowForWrite(0);
        ushort[] channels = [255 * 257, 0, 128 * 257, 10 * 257, 20 * 257, 30 * 257];
        channels.CopyTo(row);

        byte[] rgba = PictureDecoder.ToRgba(frame);

        await Assert.That(rgba).IsEquivalentTo(new byte[] { 255, 0, 128, 255, 10, 20, 30, 255 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Decode_ReadsAPngWrittenBySharpImage()
    {
        string path = Path.Combine(Path.GetTempPath(), "peepr-test-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var frame = new ImageFrame())
            {
                frame.Initialize(3, 2, SharpImage.Core.ColorspaceType.SRGB, hasAlpha: true);
                for (int y = 0; y < 2; y++)
                {
                    Span<ushort> row = frame.GetPixelRowForWrite(y);
                    for (int i = 0; i < row.Length; i++)
                    {
                        row[i] = 200 * 257;
                    }
                }
                SharpImage.Formats.FormatRegistry.Write(frame, path);
            }

            DecodedPicture picture = PictureDecoder.Decode(path);

            await Assert.That(picture.Width).IsEqualTo(3);
            await Assert.That(picture.Height).IsEqualTo(2);
            await Assert.That(picture.IsAnimated).IsFalse();
            await Assert.That((int)picture.Frames[0][0]).IsEqualTo(200);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task HalveRgba_AveragesEachTwoByTwoBlock()
    {
        // 2×2 → 1×1: channel values 0, 10, 20, 30 average (with rounding) to 15.
        byte[] source =
        [
            0, 0, 0, 255,    10, 10, 10, 255,
            20, 20, 20, 255, 30, 30, 30, 255,
        ];

        var (half, width, height) = PictureDecoder.HalveRgba(source, 2, 2);

        await Assert.That(width).IsEqualTo(1);
        await Assert.That(height).IsEqualTo(1);
        await Assert.That(half).IsEquivalentTo(new byte[] { 15, 15, 15, 255 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task HalveRgba_OddSizes_ClampAtTheEdges()
    {
        var (_, width, height) = PictureDecoder.HalveRgba(new byte[5 * 3 * 4], 5, 3);

        await Assert.That(width).IsEqualTo(2);
        await Assert.That(height).IsEqualTo(1);
    }

    [Test]
    public async Task SmallerLevels_StopOnceTheyFitAScreen()
    {
        var levels = PictureDecoder.BuildSmallerLevels(new byte[5000 * 100 * 4], 5000, 100);

        await Assert.That(levels.Length).IsEqualTo(2);
        await Assert.That(levels[0].Width).IsEqualTo(2500);
        await Assert.That(levels[1].Width).IsEqualTo(1250);
        await Assert.That(PictureDecoder.BuildSmallerLevels(new byte[2048 * 10 * 4], 2048, 10)).IsEmpty();
    }

    [Test]
    public async Task Decode_RejectsAFileThatIsNotAnImage()
    {
        string path = Path.Combine(Path.GetTempPath(), "peepr-test-" + Guid.NewGuid().ToString("N") + ".jpg");
        await File.WriteAllTextAsync(path, "definitely not a jpeg");
        try
        {
            await Assert.That(() => PictureDecoder.Decode(path)).Throws<Exception>();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
