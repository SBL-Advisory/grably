using Grably.Core.Library;
using Microsoft.Data.Sqlite;

namespace Grably.Tests.Core;

public sealed class LibraryRepositoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "grably-tests", Guid.NewGuid().ToString("N"));

    public LibraryRepositoryTests() => Directory.CreateDirectory(_folder);

    private string DbPath => Path.Combine(_folder, "library.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // release the file so the folder can be deleted
        Directory.Delete(_folder, recursive: true);
    }

    private LibraryItem Shot(string name, DateTime created, string? app = "code", string? window = null) => new()
    {
        Kind = LibraryItemKind.Screenshot,
        FilePath = Path.Combine(_folder, "Pictures", name + ".png"),
        Title = name,
        SourceApp = app,
        SourceTitle = window,
        CreatedUtc = created,
        Width = 800,
        Height = 600,
        Bytes = 12345,
    };

    [Fact]
    public void A_new_database_is_created_at_the_current_schema()
    {
        var repository = new LibraryRepository(DbPath);

        Assert.Equal(LibraryRepository.CurrentSchemaVersion, repository.SchemaVersion);
        Assert.Equal(0, repository.Count());
        Assert.Equal(1, new LibraryRepository(DbPath).SchemaVersion); // reopening does not migrate again
    }

    [Fact]
    public void A_database_from_a_newer_grably_is_refused()
    {
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<NotSupportedException>(() => new LibraryRepository(DbPath));
    }

    [Fact]
    public void Items_round_trip_and_list_newest_first()
    {
        var repository = new LibraryRepository(DbPath);
        var older = Shot("older", new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc));
        var newer = Shot("newer", new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc)) with
        {
            ProjectPath = Path.Combine(_folder, "p.grably"),
            ThumbnailPath = Path.Combine(_folder, "t.jpg"),
            SourceTitle = "Inbox - Mail",
            OcrText = "invoice total",
        };

        var olderId = repository.Add(older);
        var newerId = repository.Add(newer);
        var all = repository.Query(new LibraryQuery());

        Assert.Equal([newerId, olderId], all.Select(i => i.Id));
        Assert.Equal(newer with { Id = newerId }, all[0]);
        Assert.Equal(DateTimeKind.Utc, all[0].CreatedUtc.Kind);
    }

    [Fact]
    public void Find_by_file_and_project_ignores_case()
    {
        var repository = new LibraryRepository(DbPath);
        var item = Shot("Case", DateTime.UtcNow) with { ProjectPath = Path.Combine(_folder, "Proj.grably") };
        var id = repository.Add(item);

        Assert.Equal(id, repository.FindByFile(item.FilePath!.ToUpperInvariant())?.Id);
        Assert.Equal(id, repository.FindByProject(item.ProjectPath!.ToLowerInvariant())?.Id);
        Assert.Null(repository.FindByFile(Path.Combine(_folder, "other.png")));
    }

    [Fact]
    public void The_same_file_cannot_be_added_twice()
    {
        var repository = new LibraryRepository(DbPath);
        var item = Shot("dup", DateTime.UtcNow);
        repository.Add(item);

        Assert.Throws<SqliteException>(() => repository.Add(item with { FilePath = item.FilePath!.ToUpperInvariant() }));
    }

    [Fact]
    public void An_item_needs_a_file_or_a_project()
    {
        var repository = new LibraryRepository(DbPath);

        Assert.Throws<SqliteException>(() => repository.Add(new LibraryItem { Title = "nothing", CreatedUtc = DateTime.UtcNow }));
        repository.Add(new LibraryItem { ProjectPath = Path.Combine(_folder, "only.grably"), CreatedUtc = DateTime.UtcNow });
        Assert.Equal(1, repository.Count());
    }

    [Theory]
    [InlineData("invoice", "receipt")]
    [InlineData("INV", "receipt")]           // prefix, case-insensitive
    [InlineData("outlook", "receipt")]       // source app
    [InlineData("quarterly", "receipt")]     // window title
    [InlineData("total invoice", "receipt")] // every word, any order
    [InlineData("cafe", "menu")]             // diacritics ignored ("Café")
    public void Search_matches_title_app_window_and_text(string text, string expected)
    {
        var repository = new LibraryRepository(DbPath);
        repository.Add(Shot("receipt", DateTime.UtcNow, "OUTLOOK", "Quarterly report") with { OcrText = "Invoice 42 total due" });
        repository.Add(Shot("menu", DateTime.UtcNow, "chrome", "Café de Paris"));
        repository.Add(Shot("other", DateTime.UtcNow, "notepad"));

        var found = repository.Query(new LibraryQuery(text));

        Assert.Equal([expected], found.Select(i => i.Title));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("AND OR NOT")]
    [InlineData("title:receipt*")]
    [InlineData("(((")]
    public void Search_text_with_fts_syntax_is_treated_as_words(string text)
    {
        var repository = new LibraryRepository(DbPath);
        repository.Add(Shot("receipt", DateTime.UtcNow));

        var found = repository.Query(new LibraryQuery(text)); // must not throw

        Assert.True(found.Count <= 1);
    }

    [Fact]
    public void Search_sees_updates_and_forgets_deleted_items()
    {
        var repository = new LibraryRepository(DbPath);
        var id = repository.Add(Shot("first", DateTime.UtcNow));

        repository.Update(repository.Get(id)! with { Title = "renamed" });
        Assert.Empty(repository.Query(new LibraryQuery("first")));
        Assert.Single(repository.Query(new LibraryQuery("renamed")));

        repository.Delete(id);
        Assert.Empty(repository.Query(new LibraryQuery("renamed")));
        Assert.Null(repository.Get(id));
        Assert.Throws<KeyNotFoundException>(() => repository.Update(new LibraryItem { Id = id, FilePath = "x.png", CreatedUtc = DateTime.UtcNow }));
    }

    [Fact]
    public void Kind_filter_paging_and_counts()
    {
        var repository = new LibraryRepository(DbPath);
        for (var i = 0; i < 5; i++)
        {
            repository.Add(Shot($"s{i}", DateTime.UtcNow.AddMinutes(i)));
        }

        repository.Add(new LibraryItem { Kind = LibraryItemKind.Recording, FilePath = Path.Combine(_folder, "v.mp4"), Title = "video", CreatedUtc = DateTime.UtcNow });

        Assert.Single(repository.Query(new LibraryQuery(Kind: LibraryItemKind.Recording)));
        Assert.Equal(["s4", "s3"], repository.Query(new LibraryQuery(Kind: LibraryItemKind.Screenshot, Limit: 2)).Select(i => i.Title));
        Assert.Equal(["s2", "s1"], repository.Query(new LibraryQuery(Kind: LibraryItemKind.Screenshot, Limit: 2, Offset: 2)).Select(i => i.Title));
        Assert.Equal(5, repository.Count(LibraryItemKind.Screenshot));
        Assert.Equal(6, repository.Count());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("--!!", null)]
    [InlineData("hello world", "\"hello\"* \"world\"*")]
    [InlineData("a\"b", "\"a\"* \"b\"*")]
    public void Match_text_is_built_safely(string? text, string? expected) =>
        Assert.Equal(expected, LibraryRepository.BuildMatch(text));
}
