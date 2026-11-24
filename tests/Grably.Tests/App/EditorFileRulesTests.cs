using Grably.App;
using Grably.App.Editor;

namespace Grably.Tests.App;

public sealed class EditorFileRulesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "grably-tests", Guid.NewGuid().ToString("N"));

    public EditorFileRulesTests() => Directory.CreateDirectory(_folder);

    private string Shots => Path.Combine(_folder, "Pictures", "Grably");

    private string Projects => Path.Combine(_folder, "local", "projects");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Images_in_the_screenshots_folder_may_be_saved_over()
    {
        var png = Touch(Path.Combine(Shots, "shot.png"));
        var jpg = Touch(Path.Combine(Shots, "sub", "shot.JPG"));
        var foreignProject = Path.Combine(_folder, "Downloads", "x.grably");

        Assert.True(Trusted(png, foreignProject));
        Assert.True(Trusted(jpg, foreignProject));
    }

    [Fact]
    public void Our_own_projects_may_save_over_their_recorded_image_wherever_it_is()
    {
        var elsewhere = Touch(Path.Combine(_folder, "Desktop", "picked in save as.png"));

        Assert.True(Trusted(elsewhere, Path.Combine(Projects, "abc.grably")));
        Assert.False(Trusted(elsewhere, Path.Combine(_folder, "Downloads", "received.grably")));
    }

    [Fact]
    public void Anything_else_is_never_saved_over_without_asking()
    {
        var own = Path.Combine(Projects, "abc.grably");
        var docx = Touch(Path.Combine(Shots, "report.docx"));
        var lookalike = Touch(Path.Combine(_folder, "Pictures", "GrablyOther", "x.png")); // prefix, not inside

        Assert.False(Trusted(docx, own));
        Assert.False(Trusted(Path.Combine(Shots, "missing.png"), own));
        Assert.False(Trusted("relative.png", own));
        Assert.False(Trusted(null, own));
        Assert.False(Trusted(lookalike, Path.Combine(_folder, "Downloads", "x.grably")));
    }

    [Fact]
    public void A_second_launch_forwards_file_paths_as_absolute_paths()
    {
        var file = Touch(Path.Combine(_folder, "from-terminal.png"));
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _folder;
        try
        {
            var forwarded = Program.ForwardedArguments(["from-terminal.png", "--background", "not-a-file.png"]);

            Assert.Equal([file, "--background", "not-a-file.png"], forwarded);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    private bool Trusted(string? image, string project) =>
        EditorDocumentSource.IsTrustedImagePath(image, project, Projects, [Shots]);

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
        return path;
    }
}
