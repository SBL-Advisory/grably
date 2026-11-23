using Grably.Core.Annotations;
using Grably.Core.Library;
using Microsoft.Data.Sqlite;

namespace Grably.Tests.Core;

public sealed class LibraryScannerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "grably-tests", Guid.NewGuid().ToString("N"));

    public LibraryScannerTests()
    {
        Directory.CreateDirectory(Shots);
        Directory.CreateDirectory(Projects);
    }

    private string Shots => Path.Combine(_folder, "Pictures");

    private string Projects => Path.Combine(_folder, "projects");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private string Write(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private string Project(string name, string? imagePath)
    {
        var path = Path.Combine(Projects, name);
        ProjectFile.Save(path, [1], AnnotationDocument.ForImage(4, 4), new ProjectInfo(imagePath, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));
        return path;
    }

    [Fact]
    public void Projects_link_their_images_and_other_images_become_plain_items()
    {
        var annotated = Write(Shots, "Annotated.png");
        Write(Shots, "Plain.jpg");
        Write(Shots, "notes.txt");
        var project = Project("a.grably", annotated);

        var items = LibraryScanner.Scan(Projects, [Shots]);

        Assert.Equal(2, items.Count);
        var linked = Assert.Single(items, i => i.ProjectPath is not null);
        Assert.Equal((Path.GetFullPath(annotated), Path.GetFullPath(project), "Annotated"), (linked.FilePath, linked.ProjectPath, linked.Title));
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), linked.CreatedUtc);
        Assert.Contains(items, i => i.Title == "Plain" && i.ProjectPath is null && i.Bytes == 3);
    }

    [Fact]
    public void A_project_whose_image_is_gone_or_outside_still_becomes_an_item()
    {
        Project("gone.grably", Path.Combine(_folder, "elsewhere", "Gone.png"));
        Project("unsaved.grably", null);

        var items = LibraryScanner.Scan(Projects, [Shots]);

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Null(i.FilePath));
        Assert.Contains(items, i => i.Title == "Annotations of Gone");
    }

    [Fact]
    public void Unreadable_projects_and_missing_folders_are_skipped()
    {
        Write(Projects, "broken.grably");

        var items = LibraryScanner.Scan(Projects, [Shots, Path.Combine(_folder, "no-such-folder"), Shots]);

        Assert.Empty(items);
    }

    [Fact]
    public void AddRange_skips_files_the_library_already_has()
    {
        var repository = new LibraryRepository(Path.Combine(_folder, "library.db"));
        var file = Write(Shots, "Shot.png");
        repository.Add(new LibraryItem { FilePath = file, Title = "Shot" });

        var added = repository.AddRange(LibraryScanner.Scan(Projects, [Shots]).Append(new LibraryItem { FilePath = Write(Shots, "New.png"), Title = "New" }));

        Assert.Equal(1, added);
        Assert.Equal(2, repository.Count());
        Assert.True(repository.IsIntact());
    }
}
