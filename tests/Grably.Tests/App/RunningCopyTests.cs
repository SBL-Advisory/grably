using Grably.App.Shell;
using Grably.Platform.Shell;

namespace Grably.Tests.App;

public class RunningCopyTests
{
    private static readonly ExecutableInfo Running = new(@"D:\repos\grably\artifacts\publish\Grably.exe", "0.1.10");
    private static readonly ExecutableInfo Own = new(@"D:\repos\grably\src\Grably.App\bin\Debug\Grably.exe", "1.0.2");

    [Fact]
    public void The_question_names_both_copies_with_version_and_folder()
    {
        var text = RunningCopy.ReplaceText(Running, Own);

        Assert.Contains("Grably 0.1.10 is already running from:\nD:\\repos\\grably\\artifacts\\publish\n", text);
        Assert.Contains("You started Grably 1.0.2 from:\nD:\\repos\\grably\\src\\Grably.App\\bin\\Debug\n", text);
    }

    [Fact]
    public void An_older_copy_is_named_and_the_way_to_quit_it_explained()
    {
        var text = RunningCopy.OlderCopyText(Running, Own);

        Assert.Contains("Grably 0.1.10 is already running from:\nD:\\repos\\grably\\artifacts\\publish\n", text);
        Assert.Contains("Grably 1.0.2 from:\nD:\\repos\\grably\\src\\Grably.App\\bin\\Debug\n", text);
        Assert.Contains("tray icon", text);
    }

    [Fact]
    public void A_copy_without_a_version_is_still_named() =>
        Assert.StartsWith("Grably is already running from:", RunningCopy.ReplaceText(Running with { Version = null }, Own));
}
