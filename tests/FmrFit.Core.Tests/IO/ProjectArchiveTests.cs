using System.IO.Compression;
using FmrFit.Core.IO;

namespace FmrFit.Core.Tests.IO;

public sealed class ProjectArchiveTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("fmrfit-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private string Current()
    {
        var current = ProjectFolder.EnsureCurrent(_temp);
        File.WriteAllText(Path.Combine(current, "S1_42GHz.csv"), "a");
        File.WriteAllText(Path.Combine(current, "S1_46GHz.csv"), "bb");
        File.WriteAllText(Path.Combine(current, "S1_50GHz.csv.tmp"), "partial");
        return current;
    }

    [Fact]
    public void Archives_all_files_except_temporary_ones()
    {
        var zipPath = Path.Combine(_temp, "Projects", "S1.zip");

        var count = ProjectArchive.Save(Current(), zipPath);

        Assert.Equal(2, count);
        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(["S1_42GHz.csv", "S1_46GHz.csv"], zip.Entries.Select(e => e.FullName).Order());
        using var reader = new StreamReader(zip.GetEntry("S1_46GHz.csv")!.Open());
        Assert.Equal("bb", reader.ReadToEnd());
    }

    [Fact]
    public void Replaces_an_existing_archive()
    {
        var current = Current();
        var zipPath = Path.Combine(_temp, "Projects", "S1.zip");
        File.WriteAllText(zipPath, "old, not a zip");

        ProjectArchive.Save(current, zipPath);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(2, zip.Entries.Count);
        Assert.False(File.Exists(zipPath + ".tmp"));
    }

    [Fact]
    public void Suggested_name_is_the_most_frequent_sample()
    {
        static WorkFile File(string? sample) => new(
            new WorkFileSource(null, DateTimeOffset.Now),
            FmrMetadata.Empty with { SampleName = sample },
            null, null,
            new WorkFileProcessing(0, 0, 0, 0, 0, "", 0, 0, 0, 0, 0, 0, 10, null, null),
            [], []);

        Assert.Equal("B", ProjectArchive.SuggestName([File("A"), File("B"), File("B"), File(null)]));
        Assert.Equal(ProjectArchive.FallbackName, ProjectArchive.SuggestName([File(null), File(" ")]));
        Assert.Equal(ProjectArchive.FallbackName, ProjectArchive.SuggestName([]));
    }
}
