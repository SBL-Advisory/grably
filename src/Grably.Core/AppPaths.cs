namespace Grably.Core;

/// <summary>
/// Where Grably keeps its files. Settings roam with the user profile; the library, editable projects, recording
/// sessions and logs stay on the machine. Set the GRABLY_HOME environment variable to put everything under one
/// folder instead (used by tests and the dev harness so they never touch the real profile).
/// </summary>
public sealed class AppPaths
{
    public const string HomeEnvironmentVariable = "GRABLY_HOME";

    public AppPaths(string roamingRoot, string localRoot, string screenshotsFolder, string videosFolder)
    {
        RoamingRoot = roamingRoot;
        LocalRoot = localRoot;
        DefaultScreenshotsFolder = screenshotsFolder;
        DefaultVideosFolder = videosFolder;
    }

    /// <summary>Settings and other small per-user files that may roam.</summary>
    public string RoamingRoot { get; }

    /// <summary>Machine-local data: library, projects, sessions, thumbnails, logs.</summary>
    public string LocalRoot { get; }

    public string DefaultScreenshotsFolder { get; }

    public string DefaultVideosFolder { get; }

    public string SettingsFile => Path.Combine(RoamingRoot, "settings.json");

    public string LogsFolder => Path.Combine(LocalRoot, "logs");

    public string LibraryDatabase => Path.Combine(LocalRoot, "library.db");

    public string ProjectsFolder => Path.Combine(LocalRoot, "projects");

    public string SessionsFolder => Path.Combine(LocalRoot, "sessions");

    public string ThumbnailsFolder => Path.Combine(LocalRoot, "thumbnails");

    /// <summary>The trim editor's edit lists, one small file per video.</summary>
    public string TrimsFolder => Path.Combine(LocalRoot, "trims");

    public static AppPaths FromEnvironment()
    {
        var home = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(home))
        {
            home = Path.GetFullPath(home);
            return new AppPaths(
                Path.Combine(home, "roaming"),
                Path.Combine(home, "local"),
                Path.Combine(home, "Pictures", "Grably"),
                Path.Combine(home, "Videos", "Grably"));
        }

        return new AppPaths(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Grably"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grably"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Grably"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Grably"));
    }

    /// <summary>Creates the application's own folders. Output folders are created when something is saved.</summary>
    public void EnsureCreated()
    {
        foreach (var folder in new[] { RoamingRoot, LocalRoot, LogsFolder, ProjectsFolder, SessionsFolder, ThumbnailsFolder })
        {
            Directory.CreateDirectory(folder);
        }
    }
}
