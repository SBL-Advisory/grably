using Grably.App.Editor;

namespace Grably.Tests.App;

public class CloseCoordinatorTests
{
    [Fact]
    public void Exit_asks_only_editors_with_unsaved_work_and_then_lets_all_close()
    {
        var clean = new FakeEditor(dirty: false);
        var saved = new FakeEditor(dirty: true, answer: SaveChoice.Save);
        var discarded = new FakeEditor(dirty: true, answer: SaveChoice.Discard);

        Assert.True(CloseCoordinator.ConfirmAll([clean, saved, discarded]));

        Assert.Equal(0, clean.Asked);
        Assert.Equal(1, saved.Saves);
        Assert.Equal(0, discarded.Saves);
        Assert.All(new[] { clean, saved, discarded }, e => Assert.True(e.AllowedToClose));
    }

    [Fact]
    public void Cancel_on_any_editor_stops_the_exit_and_no_earlier_answer_sticks()
    {
        var discarded = new FakeEditor(dirty: true, answer: SaveChoice.Discard);
        var cancelled = new FakeEditor(dirty: true, answer: SaveChoice.Cancel);
        var never = new FakeEditor(dirty: true, answer: SaveChoice.Save);

        Assert.False(CloseCoordinator.ConfirmAll([discarded, cancelled, never]));

        Assert.False(discarded.AllowedToClose); // it stays open, so closing it later must ask again
        Assert.False(cancelled.AllowedToClose);
        Assert.Equal(0, never.Asked);
    }

    [Fact]
    public void A_failed_save_stops_the_exit()
    {
        var failing = new FakeEditor(dirty: true, answer: SaveChoice.Save, saveWorks: false);

        Assert.False(CloseCoordinator.ConfirmAll([failing]));
        Assert.False(failing.AllowedToClose);
    }

    [Fact]
    public void Sign_out_keeps_unsaved_work_without_asking_and_lets_everything_close()
    {
        var dirty = new FakeEditor(dirty: true, answer: SaveChoice.Cancel);
        var clean = new FakeEditor(dirty: false);

        CloseCoordinator.SessionEnding([dirty, clean]);

        Assert.Equal(1, dirty.Kept);
        Assert.Equal(0, clean.Kept);
        Assert.Equal(0, dirty.Asked);
        Assert.True(dirty.AllowedToClose);
        Assert.True(clean.AllowedToClose);
    }

    private sealed class FakeEditor(bool dirty, SaveChoice answer = SaveChoice.Cancel, bool saveWorks = true) : IUnsavedWork
    {
        public int Asked { get; private set; }

        public int Saves { get; private set; }

        public int Kept { get; private set; }

        public bool AllowedToClose { get; private set; }

        public bool HasUnsavedChanges => dirty;

        public SaveChoice AskToSave()
        {
            Asked++;
            return answer;
        }

        public bool Save()
        {
            Saves++;
            return saveWorks;
        }

        public void KeepForLater() => Kept++;

        public void AllowClose() => AllowedToClose = true;
    }
}
