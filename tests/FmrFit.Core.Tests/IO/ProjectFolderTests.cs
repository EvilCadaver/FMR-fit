using FmrFit.Core.IO;

namespace FmrFit.Core.Tests.IO;

public sealed class ProjectFolderTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("fmrfit-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void Unique_path_appends_two_digit_numbers_when_taken()
    {
        var first = ProjectFolder.UniquePath(_temp, "MM100610_cprllH", 42, "fallback");
        Assert.Equal(Path.Combine(_temp, "MM100610_cprllH_42GHz.csv"), first);

        File.WriteAllText(first, "");
        var second = ProjectFolder.UniquePath(_temp, "MM100610_cprllH", 42, "fallback");
        Assert.Equal(Path.Combine(_temp, "MM100610_cprllH_42GHz(01).csv"), second);

        File.WriteAllText(second, "");
        Assert.Equal(Path.Combine(_temp, "MM100610_cprllH_42GHz(02).csv"),
            ProjectFolder.UniquePath(_temp, "MM100610_cprllH", 42, "fallback"));
    }

    [Fact]
    public void Unique_path_uses_fallbacks_and_sanitizes_names()
    {
        Assert.Equal("source_unknownGHz.csv", Path.GetFileName(ProjectFolder.UniquePath(_temp, null, null, "source")));
        Assert.Equal("S1_9.5GHz.csv", Path.GetFileName(ProjectFolder.UniquePath(_temp, "S1", 9.5, "x")));
        Assert.Equal("a_b_c_35GHz.csv", Path.GetFileName(ProjectFolder.UniquePath(_temp, "a/b:c", 35, "x")));
    }

    [Fact]
    public void Root_is_the_repository_when_the_solution_file_is_above()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_temp, "src", "App", "bin", "Debug")).FullName;
        File.WriteAllText(Path.Combine(_temp, ProjectFolder.SolutionFileName), "");

        Assert.Equal(Path.GetFullPath(_temp), ProjectFolder.LocateRoot(bin));
    }

    [Fact]
    public void Root_is_the_application_folder_otherwise()
    {
        var app = Directory.CreateDirectory(Path.Combine(_temp, "app")).FullName;

        Assert.Equal(app, ProjectFolder.LocateRoot(app));
    }

    [Fact]
    public void Delete_removes_the_file_and_ignores_missing_ones()
    {
        var path = Path.Combine(_temp, "a_35GHz.csv");
        File.WriteAllText(path, "");

        ProjectFolder.DeleteWorkFile(path);
        ProjectFolder.DeleteWorkFile(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Ensure_creates_projects_current_and_lists_work_files_by_name()
    {
        var folder = ProjectFolder.EnsureCurrent(_temp);
        File.WriteAllText(Path.Combine(folder, "b_42GHz.csv"), "");
        File.WriteAllText(Path.Combine(folder, "a_35GHz.csv"), "");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "");

        Assert.Equal(Path.Combine(_temp, "Projects", "Current"), folder);
        Assert.Equal(["a_35GHz.csv", "b_42GHz.csv"], ProjectFolder.ListWorkFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public void Fit_file_sits_next_to_its_work_file()
    {
        Assert.Equal(Path.Combine(_temp, "a_35GHz.fit.csv"), ProjectFolder.FitFilePath(Path.Combine(_temp, "a_35GHz.csv")));
        Assert.Equal(Path.Combine(_temp, "a_35GHz(01).fit.csv"), ProjectFolder.FitFilePath(Path.Combine(_temp, "a_35GHz(01).csv")));
        Assert.Equal(Path.Combine(_temp, "S1_9.5GHz.fit.csv"), ProjectFolder.FitFilePath(Path.Combine(_temp, "S1_9.5GHz.csv")));
        Assert.True(ProjectFolder.IsFitFile("x.fit.csv"));
        Assert.True(ProjectFolder.IsFitFile("x.FIT.CSV"));
        Assert.False(ProjectFolder.IsFitFile("x.csv"));
    }

    [Fact]
    public void Fit_files_are_not_listed_as_work_files()
    {
        File.WriteAllText(Path.Combine(_temp, "a_35GHz.csv"), "");
        File.WriteAllText(Path.Combine(_temp, "a_35GHz.fit.csv"), "");

        Assert.Equal(["a_35GHz.csv"], ProjectFolder.ListWorkFiles(_temp).Select(Path.GetFileName));
    }

    [Fact]
    public void Deleting_a_work_file_also_deletes_its_fit()
    {
        var path = Path.Combine(_temp, "a_35GHz.csv");
        File.WriteAllText(path, "");
        File.WriteAllText(ProjectFolder.FitFilePath(path), "");
        File.WriteAllText(Path.Combine(_temp, "b_42GHz.fit.csv"), "");

        ProjectFolder.DeleteWorkFile(path);

        Assert.Equal(["b_42GHz.fit.csv"], Directory.GetFiles(_temp).Select(Path.GetFileName));
    }
}
