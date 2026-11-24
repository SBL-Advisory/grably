using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;

namespace Grably.Platform.Shell;

/// <summary>Another Grably asks the running one to close, so that it can start instead.</summary>
/// <param name="Executable">The Grably.exe that asks.</param>
/// <param name="Version">Its version, if known.</param>
/// <param name="ProcessId">Its process.</param>
public sealed record CloseRequest(string Executable, string? Version, int ProcessId);

/// <summary>How the running Grably answered a <see cref="CloseRequest"/>.</summary>
public enum CloseRequestResult
{
    /// <summary>The user agreed (or there was nothing to ask): it is quitting now.</summary>
    Closing,

    /// <summary>The user chose to keep it running (cancelled one of its Exit questions).</summary>
    Declined,

    /// <summary>No answer in time: it is most likely still waiting for its user.</summary>
    NoReply,

    /// <summary>The connection closed without an answer: it quit, crashed or stopped listening meanwhile.</summary>
    Gone,
}

/// <summary>
/// Keeps one Grably per user and session. The first instance owns a named mutex and listens on a named pipe; a
/// later launch sends its command line through the pipe and exits, so "open Grably again" brings up the running one.
/// Since 1.0.2 it also listens on a second pipe for <see cref="CloseRequest"/>s from a different Grably.exe; older
/// versions have no such pipe and cannot be closed from outside.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string CloseSuffix = ".close";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private Task? _closeListener;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>Takes ownership for this user and session, or returns null when another instance already has it.</summary>
    public static SingleInstanceGuard? TryAcquire(string appId)
    {
        // Local\ is per session; the user SID keeps "Run as different user" launches in the same session separate.
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, $@"Local\{appId}.SingleInstance.{UserSid()}", out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but this process may not open it: an elevated instance created it.
            return null;
        }

        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex, PipeName(appId));
    }

    /// <summary>
    /// Sends <paramref name="args"/> to the instance that owns the guard and lets it take the foreground (Windows
    /// otherwise blocks a background process from activating its window). Returns false if it did not answer.
    /// </summary>
    public static bool SendToPrimary(string appId, IReadOnlyList<string> args, TimeSpan timeout) =>
        Send(appId, args, timeout, out _);

    /// <summary>
    /// The process id of the instance that owns the guard, or null if it did not answer. It is found by connecting to
    /// its launch pipe, which has to carry a launch: an empty one opens the Library in every version, so this sends
    /// <c>--background</c>, which every Grably version ignores.
    /// </summary>
    public static int? FindPrimary(string appId, TimeSpan timeout) =>
        Send(appId, ["--background"], timeout, out var processId) && processId != 0 ? processId : null;

    /// <summary>
    /// Opens the close pipe of the instance that owns the guard, or returns null when it has none (a Grably older than
    /// 1.0.2), does not answer, or is not process <paramref name="expectedProcessId"/>. The running instance ignores
    /// the channel until a request is sent, so it can stay open while the user decides.
    /// </summary>
    public static CloseChannel? ConnectForClose(string appId, int expectedProcessId, TimeSpan timeout)
    {
        var client = new NamedPipeClientStream(
            ".", ClosePipeName(appId), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            client.Connect((int)timeout.TotalMilliseconds);
            if (PInvoke.GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid) && (int)serverPid == expectedProcessId)
            {
                return new CloseChannel(client, expectedProcessId);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // No close pipe: an older Grably, or one that is busy or elevated.
        }

        client.Dispose();
        return null;
    }

    /// <summary>Starts listening for later launches. <paramref name="onActivated"/> runs on a background thread.</summary>
    public void Listen(Action<string[]> onActivated, ILogger? logger = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        logger ??= NullLogger.Instance;
        _listener = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                    var args = line is null ? [] : JsonSerializer.Deserialize<string[]>(line) ?? [];
                    logger.LogInformation("Another launch forwarded {Count} argument(s)", args.Length);
                    onActivated(args);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Single-instance listener error");
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
        });
    }

    /// <summary>
    /// Starts accepting <see cref="CloseRequest"/>s from a different Grably.exe. <paramref name="confirm"/> asks the
    /// user (the same questions as Exit) and returns true to quit; the answer is sent back, then
    /// <paramref name="close"/> runs. Both are called on a background thread, one request at a time.
    /// </summary>
    public void ListenForCloseRequests(Func<CloseRequest, Task<bool>> confirm, Action close, ILogger? logger = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        logger ??= NullLogger.Instance;
        var pipeName = _pipeName + CloseSuffix;
        _closeListener = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, leaveOpen: true);
                    var line = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                    if (line is null || JsonSerializer.Deserialize<CloseRequest>(line) is not { } request)
                    {
                        continue; // the other launch checked that this pipe exists, then kept this Grably or gave up
                    }

                    logger.LogInformation(
                        "Grably {Version} at {Executable} (process {ProcessId}) asks this Grably to close so it can start",
                        request.Version, request.Executable, request.ProcessId);
                    var closing = await confirm(request).ConfigureAwait(false);

                    // Its questions may have taken the foreground; let the other launch show its message or window.
                    if (PInvoke.GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientPid))
                    {
                        PInvoke.AllowSetForegroundWindow(clientPid);
                    }

                    await ReplyAsync(server, closing, logger).ConfigureAwait(false);
                    if (closing)
                    {
                        // Even if the other launch stopped waiting: the user has just agreed to quit.
                        close();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Close request listener error");
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
        });
    }

    /// <summary>Stops accepting launches and close requests (call when the app starts shutting down). The mutex stays held.</summary>
    public void StopListening()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _stop.Cancel();
        try
        {
            Task.WaitAll(new[] { _listener, _closeListener }.OfType<Task>().ToArray(), TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The listeners only end by cancellation; nothing to report on shutdown.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopListening();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Disposed from a thread other than the owner; Windows releases the mutex when the process exits.
        }

        _mutex.Dispose();
        _stop.Dispose();
    }

    private static bool Send(string appId, IReadOnlyList<string> args, TimeSpan timeout, out int serverProcessId)
    {
        serverProcessId = 0;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(appId), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            if (PInvoke.GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid))
            {
                serverProcessId = (int)serverPid;
                PInvoke.AllowSetForegroundWindow(serverPid);
            }

            using var writer = new StreamWriter(client);
            writer.WriteLine(JsonSerializer.Serialize(args));
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task ReplyAsync(NamedPipeServerStream server, bool closing, ILogger logger)
    {
        try
        {
            var writer = new StreamWriter(server, leaveOpen: true);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new CloseReply(closing))).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }

            server.WaitForPipeDrain();
        }
        catch (IOException)
        {
            logger.LogInformation("The Grably that asked to close this one stopped waiting for the answer");
        }
    }

    private static string PipeName(string appId)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return $"{appId}.{UserSid()}.{process.SessionId}";
    }

    private static string ClosePipeName(string appId) => PipeName(appId) + CloseSuffix;

    private static string UserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? Environment.UserName;
    }

    private sealed record CloseReply(bool Closing);

    /// <summary>An open connection to the running Grably's close pipe (see <see cref="ConnectForClose"/>).</summary>
    public sealed class CloseChannel : IDisposable
    {
        private readonly NamedPipeClientStream _client;

        internal CloseChannel(NamedPipeClientStream client, int processId)
        {
            _client = client;
            ProcessId = processId;
        }

        /// <summary>The running Grably's process.</summary>
        public int ProcessId { get; }

        /// <summary>
        /// Asks the running Grably to quit. It asks its user first (a recording, an export, unsaved editors), so the
        /// answer can take a while; it is allowed to bring its questions to the front.
        /// </summary>
        public async Task<CloseRequestResult> RequestCloseAsync(CloseRequest request, TimeSpan replyTimeout)
        {
            using var timeout = new CancellationTokenSource(replyTimeout);
            try
            {
                PInvoke.AllowSetForegroundWindow((uint)ProcessId);
                var writer = new StreamWriter(_client, leaveOpen: true);
                await using (writer.ConfigureAwait(false))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
                    await writer.FlushAsync(timeout.Token).ConfigureAwait(false);
                }

                using var reader = new StreamReader(_client, leaveOpen: true);
                var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null)
                {
                    return CloseRequestResult.Gone;
                }

                return JsonSerializer.Deserialize<CloseReply>(line) is { } reply
                    ? reply.Closing ? CloseRequestResult.Closing : CloseRequestResult.Declined
                    : CloseRequestResult.NoReply;
            }
            catch (OperationCanceledException)
            {
                return CloseRequestResult.NoReply;
            }
            catch (IOException)
            {
                return CloseRequestResult.Gone;
            }
            catch (JsonException)
            {
                return CloseRequestResult.NoReply;
            }
        }

        public void Dispose() => _client.Dispose();
    }
}
