using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.App.Library;
using Grably.Core;
using Grably.Core.Annotations;
using Grably.Core.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Grably.Tests.App;

public sealed class LibraryServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "grably-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public LibraryServiceTests()
    {
        _paths = new AppPaths(
            Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"), Path.Combine(_folder, "Pictures"), Path.Combine(_folder, "Videos"));
        Directory.CreateDirectory(_paths.DefaultScreenshotsFolder);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private LibraryService NewService() => new(_paths, NullLogger<LibraryService>.Instance);

    private static BitmapSource Image(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0x80);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private string Screenshot(string name)
    {
        var path = Path.Combine(_paths.DefaultScreenshotsFolder, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private static string[] Thumbnails(AppPaths paths) =>
        Directory.Exists(paths.ThumbnailsFolder) ? Directory.GetFiles(paths.ThumbnailsFolder) : [];

    [Fact]
    public void A_first_save_adds_an_item_with_a_bounded_thumbnail() => StaRunner.Run(() =>
    {
        var library = NewService();
        var changes = 0;
        library.Changed += (_, _) => changes++;
        var file = Screenshot("Shot.png");
        var project = Path.Combine(_folder, "Shot.grably");

        var item = library.RecordSave(file, project, Image(1200, 400))!;

        Assert.Equal(1, changes);
        var stored = library.Repository.Get(item.Id)!;
        Assert.Equal("Shot", stored.Title);
        Assert.Equal(project, stored.ProjectPath);
        Assert.Equal((1200, 400), (stored.Width, stored.Height));
        Assert.Equal(4, stored.Bytes);
        var thumbnail = BitmapFrame.Create(new Uri(stored.ThumbnailPath!), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(LibraryService.ThumbnailWidth, thumbnail.PixelWidth); // scaled down to fit, keeping the shape
        Assert.Equal(120, thumbnail.PixelHeight);
    });

    [Fact]
    public void Saving_again_updates_the_same_item_and_replaces_its_thumbnail() => StaRunner.Run(() =>
    {
        var library = NewService();
        var file = Screenshot("Shot.png");
        var project = Path.Combine(_folder, "Shot.grably");
        var first = library.RecordSave(file, project, Image(200, 100))!;

        var second = library.RecordSave(file, project, Image(300, 150))!;

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, library.Repository.Count());
        Assert.Equal(300, library.Repository.Get(first.Id)!.Width);
        Assert.Equal([second.ThumbnailPath!], Thumbnails(_paths)); // the old thumbnail is gone
    });

    [Fact]
    public void Kept_annotations_of_an_image_grably_may_not_overwrite_get_their_own_item() => StaRunner.Run(() =>
    {
        var library = NewService();
        var project = Path.Combine(_folder, "kept.grably");

        library.RecordKeptProject(filePath: null, project, "photo.png", Image(64, 64));

        var item = Assert.Single(library.Repository.Query(new LibraryQuery()));
        Assert.Null(item.FilePath);
        Assert.Equal(project, item.ProjectPath);
        Assert.Equal("Unsaved annotations of photo.png", item.Title);
    });

    [Fact]
    public void A_project_only_item_is_named_after_its_file_once_saved() => StaRunner.Run(() =>
    {
        var library = NewService();
        var project = Path.Combine(_folder, "kept.grably");
        library.RecordKeptProject(filePath: null, project, "photo.png", Image(64, 64));

        var saved = library.RecordSave(Screenshot("Annotated photo.png"), project, Image(64, 64))!;

        Assert.Equal(1, library.Repository.Count());
        Assert.Equal("Annotated photo", library.Repository.Get(saved.Id)!.Title);
    });

    [Fact]
    public void A_damaged_database_is_set_aside_and_a_new_one_started()
    {
        Directory.CreateDirectory(_paths.LocalRoot);
        File.WriteAllText(_paths.LibraryDatabase, "this is not a database, it is long enough to have a header area");

        var library = NewService();

        Assert.Equal(0, library.Repository.Count());
        var aside = Assert.Single(Directory.GetFiles(_paths.LocalRoot, "library.db.broken-*"));
        Assert.StartsWith("this is not a database", File.ReadAllText(aside));
    }

    [Fact]
    public void A_new_library_starts_with_the_captures_and_projects_already_on_disk()
    {
        var annotated = Screenshot("Annotated.png");
        Screenshot("Plain.png");
        Directory.CreateDirectory(_paths.ProjectsFolder);
        var project = Path.Combine(_paths.ProjectsFolder, "abc.grably");
        ProjectFile.Save(project, [1], AnnotationDocument.ForImage(4, 4), new ProjectInfo(annotated, DateTime.UtcNow));

        var library = NewService();

        Assert.Equal(2, library.Repository.Count());
        Assert.Equal(Path.GetFullPath(project), library.Repository.FindByFile(annotated)!.ProjectPath);
        Assert.Null(library.Repository.FindByFile(Path.Combine(_paths.DefaultScreenshotsFolder, "Plain.png"))!.ProjectPath);

        File.Delete(Path.Combine(_paths.DefaultScreenshotsFolder, "Plain.png"));
        Assert.Equal(2, NewService().Repository.Count()); // only a new library is filled; an existing one is left as is
    }

    [Fact]
    public void Renaming_points_the_item_at_the_new_file() => StaRunner.Run(() =>
    {
        var library = NewService();
        var item = library.RecordSave(Screenshot("Shot.png"), Path.Combine(_folder, "Shot.grably"), Image(64, 64))!;
        var target = Path.Combine(_paths.DefaultScreenshotsFolder, "Login page.png");

        var renamed = library.Rename(item, target)!;

        Assert.Equal(target, renamed.FilePath);
        Assert.Equal("Login page", library.Repository.Get(item.Id)!.Title);
        Assert.Equal(item.Id, library.Repository.FindByFile(target)!.Id);
    });

    [Fact]
    public void Kept_annotations_of_a_library_file_link_the_project_to_its_item() => StaRunner.Run(() =>
    {
        var library = NewService();
        var file = Screenshot("Shot.png");
        var item = library.RecordSave(file, Path.Combine(_folder, "old.grably"), Image(64, 64))!;
        var project = Path.Combine(_folder, "kept.grably");

        library.RecordKeptProject(file, project, "Shot.png", Image(64, 64));

        Assert.Equal(1, library.Repository.Count());
        Assert.Equal(project, library.Repository.Get(item.Id)!.ProjectPath);
    });

    [Fact]
    public void Removing_deletes_thumbnails_but_never_files_outside_the_thumbnails_folder() => StaRunner.Run(() =>
    {
        var library = NewService();
        var item = library.RecordSave(Screenshot("Shot.png"), Path.Combine(_folder, "Shot.grably"), Image(64, 64))!;

        // A database pointing elsewhere, including a folder whose name merely starts with "thumbnails".
        var lookalike = Path.Combine(_paths.LocalRoot, "thumbnails-old", "keep.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(lookalike)!);
        File.WriteAllBytes(lookalike, [1]);
        var outsider = library.RecordSave(Screenshot("Other.png"), Path.Combine(_folder, "Other.grably"), Image(64, 64))!;
        library.Repository.Update(outsider with { ThumbnailPath = lookalike });

        library.Remove([item, outsider with { ThumbnailPath = lookalike }]);

        Assert.Equal(0, library.Repository.Count());
        Assert.False(File.Exists(item.ThumbnailPath));
        Assert.True(File.Exists(lookalike));
        Assert.True(File.Exists(Path.Combine(_paths.DefaultScreenshotsFolder, "Shot.png"))); // files are the caller's job
    });

    [Fact]
    public void An_item_is_missing_only_when_neither_its_file_nor_its_project_exists()
    {
        var file = Screenshot("Shot.png");
        var project = Path.Combine(_folder, "Shot.grably");
        var item = new LibraryItem { FilePath = file, ProjectPath = project, Title = "Shot" };

        Assert.False(LibraryService.IsMissing(item));
        File.Delete(file);
        Assert.True(LibraryService.IsMissing(item));
        File.WriteAllBytes(project, [1]);
        Assert.False(LibraryService.IsMissing(item)); // the annotations can still be opened
        Assert.True(LibraryService.IsMissing(new LibraryItem { ProjectPath = Path.Combine(_folder, "gone.grably"), Title = "x" }));
    }
}
