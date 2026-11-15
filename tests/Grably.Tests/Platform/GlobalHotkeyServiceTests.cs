using Grably.Core.Hotkeys;
using Grably.Platform.Hotkeys;
using Grably.Platform.Input;

namespace Grably.Tests.Platform;

/// <summary>Needs an unlocked interactive desktop: registers real global hotkeys and presses them with SendInput.</summary>
[Trait("Category", "Desktop")]
public class GlobalHotkeyServiceTests
{
    private static readonly HotkeyGesture TestGesture = HotkeyGesture.Parse("Ctrl+Alt+Shift+F23");
    private static readonly HotkeyGesture OtherGesture = HotkeyGesture.Parse("Ctrl+Alt+Shift+F20");

    [Fact]
    public async Task Registered_hotkey_is_raised_when_pressed()
    {
        using var hotkeys = new GlobalHotkeyService();
        var received = new TaskCompletionSource<HotkeyAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        hotkeys.HotkeyPressed += (_, e) => received.TrySetResult(e.Action);

        var registration = hotkeys.Register(HotkeyAction.CaptureText, TestGesture);
        Assert.True(registration.Succeeded, $"RegisterHotKey failed with Win32 error {registration.ErrorCode}");

        InputSimulator.SendGesture(TestGesture);

        var action = await received.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(HotkeyAction.CaptureText, action);
    }

    [Fact]
    public void Apply_replaces_its_own_registrations_and_skips_turned_off_hotkeys()
    {
        using var hotkeys = new GlobalHotkeyService();

        var first = hotkeys.Apply([new(HotkeyAction.CaptureText, TestGesture)]);
        var second = hotkeys.Apply(
        [
            new(HotkeyAction.CaptureText, TestGesture), // must not conflict with the registration it replaces
            new(HotkeyAction.ScrollingCapture, HotkeyGesture.None),
        ]);

        Assert.True(Assert.Single(first).Succeeded);
        var only = Assert.Single(second);
        Assert.True(only.Succeeded);
        Assert.Equal(HotkeyAction.CaptureText, only.Action);
    }

    [Fact]
    public async Task Apply_keeps_hotkeys_added_with_Register()
    {
        using var hotkeys = new GlobalHotkeyService();
        var received = new TaskCompletionSource<HotkeyAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        hotkeys.HotkeyPressed += (_, e) => received.TrySetResult(e.Action);

        Assert.True(hotkeys.Register(HotkeyAction.PauseRecording, OtherGesture).Succeeded);
        hotkeys.Apply([new(HotkeyAction.CaptureText, TestGesture)]);

        InputSimulator.SendGesture(OtherGesture);

        var action = await received.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(HotkeyAction.PauseRecording, action);
    }

    [Fact]
    public async Task Handlers_can_change_registrations_from_the_hotkey_thread()
    {
        using var hotkeys = new GlobalHotkeyService();
        var unregistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hotkeys.HotkeyPressed += (_, _) =>
        {
            hotkeys.Unregister(HotkeyAction.PauseRecording); // would deadlock if Invoke always posted and waited
            unregistered.TrySetResult();
        };
        hotkeys.Register(HotkeyAction.PauseRecording, OtherGesture);

        InputSimulator.SendGesture(OtherGesture);

        await unregistered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Temporary_hotkey_calls_back_and_is_released_on_dispose()
    {
        using var hotkeys = new GlobalHotkeyService();
        var pressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var raisedAsAction = false;
        hotkeys.HotkeyPressed += (_, _) => raisedAsAction = true;

        var temporary = hotkeys.RegisterTemporary(OtherGesture, () => pressed.TrySetResult());
        Assert.NotNull(temporary);
        InputSimulator.SendGesture(OtherGesture);
        await pressed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(raisedAsAction);

        temporary.Dispose();

        // Released: the same combination can be registered again.
        Assert.True(hotkeys.Register(HotkeyAction.PauseRecording, OtherGesture).Succeeded);
    }

    [Fact]
    public void Calls_after_dispose_throw()
    {
        var hotkeys = new GlobalHotkeyService();
        hotkeys.Dispose();

        Assert.Throws<ObjectDisposedException>(() => hotkeys.Register(HotkeyAction.CaptureText, TestGesture));
    }
}
