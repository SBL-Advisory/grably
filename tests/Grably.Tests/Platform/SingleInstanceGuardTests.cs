using Grably.Platform.Shell;

namespace Grably.Tests.Platform;

/// <summary>Uses a unique app id per test so tests never collide with each other or with a running Grably.</summary>
public class SingleInstanceGuardTests
{
    private static readonly CloseRequest Request = new(@"D:\Other\Grably.exe", "9.9.9", 4242);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string NewAppId() => $"GrablyTest-{Guid.NewGuid():N}";

    private static Task<SingleInstanceGuard.CloseChannel?> ConnectForCloseAsync(string appId, TimeSpan timeout) =>
        Task.Run(() => SingleInstanceGuard.ConnectForClose(appId, Environment.ProcessId, timeout), Token);

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
    public async Task Finding_the_primary_returns_its_process_and_sends_only_a_background_launch()
    {
        var appId = NewAppId();
        using var guard = SingleInstanceGuard.TryAcquire(appId)!;
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        guard.Listen(args => received.TrySetResult(args));

        var processId = await Task.Run(() => SingleInstanceGuard.FindPrimary(appId, TimeSpan.FromSeconds(5)), Token);

        Assert.Equal(Environment.ProcessId, processId);
        // Every Grably version ignores a --background launch; an empty one would open its Library.
        Assert.Equal(["--background"], await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public void Finding_the_primary_fails_when_no_instance_is_running() =>
        Assert.Null(SingleInstanceGuard.FindPrimary(NewAppId(), TimeSpan.FromMilliseconds(200)));

    [Fact]
    public async Task A_primary_that_only_listens_for_launches_cannot_be_asked_to_close()
    {
        // What a Grably older than 1.0.2 looks like from outside.
        var appId = NewAppId();
        using var guard = SingleInstanceGuard.TryAcquire(appId)!;
        guard.Listen(_ => { });

        var channel = await ConnectForCloseAsync(appId, TimeSpan.FromMilliseconds(300));

        Assert.Null(channel);
    }

    [Fact]
    public async Task The_close_pipe_of_another_process_is_not_used()
    {
        var appId = NewAppId();
        using var guard = SingleInstanceGuard.TryAcquire(appId)!;
        guard.ListenForCloseRequests(_ => Task.FromResult(true), () => { });

        var channel = await Task.Run(
            () => SingleInstanceGuard.ConnectForClose(appId, Environment.ProcessId + 1, TimeSpan.FromSeconds(5)), Token);

        Assert.Null(channel);
    }

    [Fact]
    public async Task A_close_request_the_user_agrees_to_is_answered_and_then_closes()
    {
        var appId = NewAppId();
        using var guard = SingleInstanceGuard.TryAcquire(appId)!;
        CloseRequest? asked = null;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        guard.ListenForCloseRequests(request => { asked = request; return Task.FromResult(true); }, () => closed.TrySetResult());

        using var channel = await ConnectForCloseAsync(appId, TimeSpan.FromSeconds(5));
        var result = await channel!.RequestCloseAsync(Request, TimeSpan.FromSeconds(5));

        Assert.Equal(CloseRequestResult.Closing, result);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(Request, asked);
    }

    [Fact]
    public async Task A_close_request_the_user_cancels_is_declined_and_nothing_closes()
    {
        var appId = NewAppId();
        using var guard = SingleInstanceGuard.TryAcquire(appId)!;
        var closeCalls = 0;
        guard.ListenForCloseRequests(_ => Task.FromResult(false), () => Interlocked.Increment(ref closeCalls));

        using var channel = await ConnectForCloseAsync(appId, TimeSpan.FromSeconds(5));
        var result = await channel!.RequestCloseAsync(Request, TimeSpan.FromSeconds(5));

        Assert.Equal(CloseRequestResult.Declined, result);
        Assert.Equal(0, Volatile.Read(ref closeCalls));
    }

    [Fact]
    public async Task A_close_request_without_an_answer_in_time_gets_no_reply()
    {
        var appId = NewAppId();
        using var guard = SingleInstanceGuard.TryAcquire(appId)!;
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        guard.ListenForCloseRequests(_ => answer.Task, () => { });

        using var channel = await ConnectForCloseAsync(appId, TimeSpan.FromSeconds(5));
        var result = await channel!.RequestCloseAsync(Request, TimeSpan.FromMilliseconds(300));

        Assert.Equal(CloseRequestResult.NoReply, result);
        answer.TrySetResult(false); // let the listener finish before the guard is disposed
    }

    [Fact]
    public async Task A_primary_that_goes_away_mid_request_is_reported_as_gone_at_once()
    {
        var appId = NewAppId();
        using var guard = SingleInstanceGuard.TryAcquire(appId)!;
        var closeCalls = 0;
        guard.ListenForCloseRequests(
            _ => throw new InvalidOperationException("the primary failed while asking"), () => Interlocked.Increment(ref closeCalls));

        using var channel = await ConnectForCloseAsync(appId, TimeSpan.FromSeconds(5));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await channel!.RequestCloseAsync(Request, TimeSpan.FromSeconds(30));

        Assert.Equal(CloseRequestResult.Gone, result);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.Equal(0, Volatile.Read(ref closeCalls));
    }

    [Fact]
    public async Task A_channel_closed_without_a_request_is_ignored()
    {
        var appId = NewAppId();
        using var guard = SingleInstanceGuard.TryAcquire(appId)!;
        var asked = 0;
        guard.ListenForCloseRequests(_ => Task.FromResult(Interlocked.Increment(ref asked) < 0), () => { });

        // The other launch showed its dialog and the user kept this Grably.
        (await ConnectForCloseAsync(appId, TimeSpan.FromSeconds(5)))!.Dispose();
        using var channel = await ConnectForCloseAsync(appId, TimeSpan.FromSeconds(5));
        var result = await channel!.RequestCloseAsync(Request, TimeSpan.FromSeconds(5));

        Assert.Equal(CloseRequestResult.Declined, result);
        Assert.Equal(1, Volatile.Read(ref asked));
    }

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
