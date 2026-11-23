using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Grably.Core.Library;

/// <summary>
/// The capture library in SQLite (%LocalAppData%\Grably\library.db). Full-text search over title, app, window
/// title and recognised text uses FTS5, kept in sync with the items table by triggers. The schema version lives in
/// PRAGMA user_version; <see cref="Migrations"/> upgrades older databases in place.
/// </summary>
public sealed class LibraryRepository
{
    private const string Columns =
        "id, kind, file_path, project_path, thumb_path, title, source_app, source_title, created_utc, width, height, duration_ms, bytes, ocr_text";

    /// <summary>Index = the version the step upgrades to (step 0 creates version 1).</summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE items (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind TEXT NOT NULL,
            file_path TEXT,
            project_path TEXT,
            thumb_path TEXT,
            title TEXT NOT NULL DEFAULT '',
            source_app TEXT,
            source_title TEXT,
            created_utc TEXT NOT NULL,
            width INTEGER NOT NULL DEFAULT 0,
            height INTEGER NOT NULL DEFAULT 0,
            duration_ms INTEGER,
            bytes INTEGER NOT NULL DEFAULT 0,
            ocr_text TEXT,
            CHECK (file_path IS NOT NULL OR project_path IS NOT NULL));
        CREATE INDEX ix_items_created ON items(created_utc DESC);
        CREATE UNIQUE INDEX ix_items_file ON items(file_path COLLATE NOCASE) WHERE file_path IS NOT NULL;
        CREATE INDEX ix_items_project ON items(project_path COLLATE NOCASE) WHERE project_path IS NOT NULL;
        CREATE VIRTUAL TABLE items_fts USING fts5(
            title, source_app, source_title, ocr_text,
            content='items', content_rowid='id', tokenize='unicode61 remove_diacritics 2');
        CREATE TRIGGER items_ai AFTER INSERT ON items BEGIN
            INSERT INTO items_fts(rowid, title, source_app, source_title, ocr_text)
            VALUES (new.id, new.title, new.source_app, new.source_title, new.ocr_text);
        END;
        CREATE TRIGGER items_ad AFTER DELETE ON items BEGIN
            INSERT INTO items_fts(items_fts, rowid, title, source_app, source_title, ocr_text)
            VALUES ('delete', old.id, old.title, old.source_app, old.source_title, old.ocr_text);
        END;
        CREATE TRIGGER items_au AFTER UPDATE ON items BEGIN
            INSERT INTO items_fts(items_fts, rowid, title, source_app, source_title, ocr_text)
            VALUES ('delete', old.id, old.title, old.source_app, old.source_title, old.ocr_text);
            INSERT INTO items_fts(rowid, title, source_app, source_title, ocr_text)
            VALUES (new.id, new.title, new.source_app, new.source_title, new.ocr_text);
        END;
        """,
    ];

    private readonly string _connectionString;

    public LibraryRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        Initialize();
    }

    public static int CurrentSchemaVersion => Migrations.Length;

    public int SchemaVersion
    {
        get
        {
            using var connection = Open();
            return ReadVersion(connection);
        }
    }

    public long Add(LibraryItem item)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO items (kind, file_path, project_path, thumb_path, title, source_app, source_title,
                               created_utc, width, height, duration_ms, bytes, ocr_text)
            VALUES ($kind, $file, $project, $thumb, $title, $app, $window, $created, $width, $height, $duration, $bytes, $ocr);
            SELECT last_insert_rowid();
            """;
        Bind(command, item);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Adds many items in one transaction, skipping any whose file is already in the library.</summary>
    /// <returns>How many were added.</returns>
    public int AddRange(IEnumerable<LibraryItem> items)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var added = 0;
        foreach (var item in items)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO items (kind, file_path, project_path, thumb_path, title, source_app, source_title,
                                             created_utc, width, height, duration_ms, bytes, ocr_text)
                VALUES ($kind, $file, $project, $thumb, $title, $app, $window, $created, $width, $height, $duration, $bytes, $ocr);
                """;
            Bind(command, item);
            added += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return added;
    }

    /// <summary>SQLite's quick integrity check. A damaged file can open fine and only fail when its pages are read.</summary>
    public bool IsIntact()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        return command.ExecuteScalar() is string result && result == "ok";
    }

    /// <summary>Replaces every field of the item with this id.</summary>
    public void Update(LibraryItem item)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE items SET kind = $kind, file_path = $file, project_path = $project, thumb_path = $thumb,
                title = $title, source_app = $app, source_title = $window, created_utc = $created, width = $width,
                height = $height, duration_ms = $duration, bytes = $bytes, ocr_text = $ocr
            WHERE id = $id;
            """;
        Bind(command, item);
        command.Parameters.AddWithValue("$id", item.Id);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new KeyNotFoundException($"There is no library item {item.Id}.");
        }
    }

    public void Delete(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public LibraryItem? Get(long id) => QuerySingle("id = $value", id);

    /// <summary>The item for an image or video file (paths compare case-insensitively, as on Windows).</summary>
    public LibraryItem? FindByFile(string path) => QuerySingle("file_path = $value COLLATE NOCASE", Path.GetFullPath(path));

    public LibraryItem? FindByProject(string path) => QuerySingle("project_path = $value COLLATE NOCASE", Path.GetFullPath(path));

    public IReadOnlyList<LibraryItem> Query(LibraryQuery query)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = new List<string>();
        if (query.Kind is { } kind)
        {
            where.Add("kind = $kind");
            command.Parameters.AddWithValue("$kind", kind.ToString());
        }

        var match = BuildMatch(query.Text);
        if (match is not null)
        {
            where.Add("id IN (SELECT rowid FROM items_fts WHERE items_fts MATCH $match)");
            command.Parameters.AddWithValue("$match", match);
        }

        command.CommandText = $"""
            SELECT {Columns} FROM items
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty)}
            ORDER BY created_utc DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 100_000));
        command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        return Read(command);
    }

    public int Count(LibraryItemKind? kind = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = kind is null ? "SELECT COUNT(*) FROM items;" : "SELECT COUNT(*) FROM items WHERE kind = $kind;";
        if (kind is { } k)
        {
            command.Parameters.AddWithValue("$kind", k.ToString());
        }

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Turns what the user typed into an FTS5 query: every word must match, each as a prefix ("scre" finds
    /// "Screenshot"). Words are quoted, so FTS syntax in the text (AND, NOT, quotes, colons) is just text.
    /// Returns null when there is nothing to search for.
    /// </summary>
    public static string? BuildMatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words.Count == 0 ? null : string.Join(' ', words.Select(w => $"\"{w}\"*"));
    }

    private void Initialize()
    {
        using var connection = Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }

        var version = ReadVersion(connection);
        if (version > Migrations.Length)
        {
            throw new NotSupportedException(
                $"The library was created by a newer Grably (schema {version}); update Grably to open it.");
        }

        for (; version < Migrations.Length; version++)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = Migrations[version] + $"\nPRAGMA user_version = {version + 1};";
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private LibraryItem? QuerySingle(string condition, object value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM items WHERE {condition} LIMIT 1;";
        command.Parameters.AddWithValue("$value", value);
        return Read(command).FirstOrDefault();
    }

    private static void Bind(SqliteCommand command, LibraryItem item)
    {
        static object Db(object? value) => value ?? DBNull.Value;
        command.Parameters.AddWithValue("$kind", item.Kind.ToString());
        command.Parameters.AddWithValue("$file", Db(item.FilePath is null ? null : Path.GetFullPath(item.FilePath)));
        command.Parameters.AddWithValue("$project", Db(item.ProjectPath is null ? null : Path.GetFullPath(item.ProjectPath)));
        command.Parameters.AddWithValue("$thumb", Db(item.ThumbnailPath));
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$app", Db(item.SourceApp));
        command.Parameters.AddWithValue("$window", Db(item.SourceTitle));
        command.Parameters.AddWithValue("$created", item.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$width", item.Width);
        command.Parameters.AddWithValue("$height", item.Height);
        command.Parameters.AddWithValue("$duration", Db(item.DurationMs));
        command.Parameters.AddWithValue("$bytes", item.Bytes);
        command.Parameters.AddWithValue("$ocr", Db(item.OcrText));
    }

    private static List<LibraryItem> Read(SqliteCommand command)
    {
        var items = new List<LibraryItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new LibraryItem
            {
                Id = reader.GetInt64(0),
                Kind = Enum.TryParse<LibraryItemKind>(reader.GetString(1), out var kind) ? kind : LibraryItemKind.Screenshot,
                FilePath = reader.IsDBNull(2) ? null : reader.GetString(2),
                ProjectPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                ThumbnailPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                Title = reader.GetString(5),
                SourceApp = reader.IsDBNull(6) ? null : reader.GetString(6),
                SourceTitle = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedUtc = DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Width = reader.GetInt32(9),
                Height = reader.GetInt32(10),
                DurationMs = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                Bytes = reader.GetInt64(12),
                OcrText = reader.IsDBNull(13) ? null : reader.GetString(13),
            });
        }

        return items;
    }
}
