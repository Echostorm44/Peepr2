namespace Peepr.Tests;

internal sealed class NaturalStringComparerTests
{
    [Test]
    public async Task DigitRuns_CompareByValue_LikeExplorer()
    {
        string[] names = ["img10.jpg", "img2.jpg", "IMG1.jpg", "img02b.jpg", "a.jpg"];
        Array.Sort(names, NaturalStringComparer.Instance);

        await Assert.That(names).IsEquivalentTo(["a.jpg", "IMG1.jpg", "img2.jpg", "img02b.jpg", "img10.jpg"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task VeryLongDigitRuns_DoNotOverflow()
    {
        int order = NaturalStringComparer.Instance.Compare("shot99999999999999999999.png", "shot100000000000000000000.png");

        await Assert.That(order).IsLessThan(0);
    }

    [Test]
    public async Task LeadingZeros_AreIgnoredForValue()
    {
        await Assert.That(NaturalStringComparer.Instance.Compare("frame007", "frame7")).IsEqualTo(0);
    }
}

internal sealed class MediaTypesTests
{
    [Test]
    [Arguments(@"C:\photos\a.JPG", MediaKind.Image)]
    [Arguments(@"C:\photos\b.heic", MediaKind.Image)]
    [Arguments(@"C:\photos\c.MKV", MediaKind.Video)]
    [Arguments(@"C:\photos\d.webm", MediaKind.Video)]
    public async Task KindOf_ReadsTheExtension(string path, MediaKind expected)
    {
        await Assert.That(MediaTypes.IsSupported(path)).IsTrue();
        await Assert.That(MediaTypes.KindOf(path)).IsEqualTo(expected);
    }

    [Test]
    public async Task UnknownExtensions_AreNotSupported()
    {
        await Assert.That(MediaTypes.IsSupported(@"C:\docs\notes.txt")).IsFalse();
        await Assert.That(MediaTypes.IsSupported(@"C:\docs\noextension")).IsFalse();
    }
}

internal sealed class MediaFolderTests
{
    // A folder of four viewable files (one video) and one that isn't, with distinct sizes and dates.
    private static string CreateFolder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "peepr-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        (string Name, int Size, int AgeDays)[] files =
        [
            ("b10.jpg", 300, 1),
            ("b2.png", 100, 3),
            ("a.mp4", 400, 2),
            ("c.webp", 200, 4),
            ("readme.txt", 999, 0),
        ];
        foreach (var (name, size, age) in files)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, new byte[size]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-age));
        }
        return directory;
    }

    private static string[] Names(MediaFolder folder) => folder.Files.Select(Path.GetFileName).ToArray()!;

    [Test]
    [Arguments(SortOrder.Name, new[] { "a.mp4", "b2.png", "b10.jpg", "c.webp" })]
    [Arguments(SortOrder.NewestFirst, new[] { "b10.jpg", "a.mp4", "b2.png", "c.webp" })]
    [Arguments(SortOrder.OldestFirst, new[] { "c.webp", "b2.png", "a.mp4", "b10.jpg" })]
    [Arguments(SortOrder.LargestFirst, new[] { "a.mp4", "b10.jpg", "c.webp", "b2.png" })]
    [Arguments(SortOrder.Type, new[] { "b10.jpg", "a.mp4", "b2.png", "c.webp" })]
    public async Task Open_ListsViewableFiles_InTheChosenOrder(SortOrder order, string[] expected)
    {
        string directory = CreateFolder();
        try
        {
            var folder = MediaFolder.Open(Path.Combine(directory, "b2.png"), order);

            await Assert.That(Names(folder)).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(Path.GetFileName(folder.CurrentFile)).IsEqualTo("b2.png");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task OpenDirectory_StartsOnTheFirstFile()
    {
        string directory = CreateFolder();
        try
        {
            var folder = MediaFolder.OpenDirectory(directory, SortOrder.Name);

            await Assert.That(folder.CurrentIndex).IsEqualTo(0);
            await Assert.That(Path.GetFileName(folder.CurrentFile)).IsEqualTo("a.mp4");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Navigation_WrapsAtBothEnds()
    {
        string directory = CreateFolder();
        try
        {
            var folder = MediaFolder.OpenDirectory(directory, SortOrder.Name);

            folder.MoveBy(-1);
            await Assert.That(Path.GetFileName(folder.CurrentFile)).IsEqualTo("c.webp");
            folder.MoveBy(1);
            await Assert.That(Path.GetFileName(folder.CurrentFile)).IsEqualTo("a.mp4");
            await Assert.That(Path.GetFileName(folder.FileAt(-1))).IsEqualTo("c.webp");
            await Assert.That(Path.GetFileName(folder.FileAt(5))).IsEqualTo("b2.png");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RemoveThenRestore_PutsTheFileBackWhereItWas()
    {
        string directory = CreateFolder();
        try
        {
            var folder = MediaFolder.Open(Path.Combine(directory, "b2.png"), SortOrder.Name);

            var removed = folder.RemoveCurrent();
            await Assert.That(Path.GetFileName(folder.CurrentFile)).IsEqualTo("b10.jpg");
            await Assert.That(folder.Count).IsEqualTo(3);

            folder.Restore(removed!.Value.Path, removed.Value.Index);
            await Assert.That(Names(folder)).IsEquivalentTo(["a.mp4", "b2.png", "b10.jpg", "c.webp"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(Path.GetFileName(folder.CurrentFile)).IsEqualTo("b2.png");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RemovingTheLastFile_StepsBackToTheNewLast()
    {
        string directory = CreateFolder();
        try
        {
            var folder = MediaFolder.Open(Path.Combine(directory, "c.webp"), SortOrder.Name);

            folder.RemoveCurrent();

            await Assert.That(Path.GetFileName(folder.CurrentFile)).IsEqualTo("b10.jpg");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Resort_KeepsTheSameFileOnScreen()
    {
        string directory = CreateFolder();
        try
        {
            var folder = MediaFolder.Open(Path.Combine(directory, "b10.jpg"), SortOrder.Name);

            folder.Resort(SortOrder.LargestFirst);

            await Assert.That(Path.GetFileName(folder.CurrentFile)).IsEqualTo("b10.jpg");
            await Assert.That(folder.CurrentIndex).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task OpeningAnUnlistedFile_StillShowsIt()
    {
        string directory = CreateFolder();
        try
        {
            var folder = MediaFolder.Open(Path.Combine(directory, "readme.txt"), SortOrder.Name);

            await Assert.That(Path.GetFileName(folder.CurrentFile)).IsEqualTo("readme.txt");
            await Assert.That(folder.Count).IsEqualTo(5);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
