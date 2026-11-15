using Grably.Platform.Shell;

namespace Grably.Tests.Platform;

/// <summary>Uses a unique app id per test so tests never collide with each other or with a running Grably.</summary>
public class SingleInstanceGuardTests
{
    private static string NewAppId() => $"GrablyTest-{Guid.NewGuid():N}";

    [Fact]
    public void Second_acquire_fails_while_the_first_is_held()
    {
        var appId = NewAppId();
        using var first = SingleInstanceGuard.TryAcquire(appId);

        Assert.NotNull(first);
        Assert.Null(SingleInstanceGuard.TryAcquire(appId));
    }

    [Fact]
    public void Can_acquire_again_after_dispose()
    {
        var appId = NewAppId();
        SingleInstanceGuard.TryAcquire(appId)!.Dispose();

        using var again = SingleInstanceGuard.TryAcquire(appId);

        Assert.NotNull(again);
    }

    [Fact]
    public async Task Arguments_sent_by_a_later_launch_reach_the_listener()
    {
        var appId = NewAppId();
        using var guard = SingleInstanceGuard.TryAcquire(appId)!;
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        guard.Listen(args => received.TrySetResult(args));

        var sent = await Task.Run(
            () => SingleInstanceGuard.SendToPrimary(appId, ["--background", "a b"], TimeSpan.FromSeconds(5)),
            TestContext.Current.CancellationToken);

        Assert.True(sent);
        var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["--background", "a b"], args);
    }

    [Fact]
    public void Sending_fails_when_no_instance_is_running() =>
        Assert.False(SingleInstanceGuard.SendToPrimary(NewAppId(), [], TimeSpan.FromMilliseconds(200)));

    [Fact]
    public async Task Stopped_listener_no_longer_accepts_launches()
    {
        var appId = NewAppId();
        using var guard = SingleInstanceGuard.TryAcquire(appId)!;
        guard.Listen(_ => { });
        guard.StopListening();

        var sent = await Task.Run(
            () => SingleInstanceGuard.SendToPrimary(appId, [], TimeSpan.FromMilliseconds(300)),
            TestContext.Current.CancellationToken);

        Assert.False(sent);
    }
}
