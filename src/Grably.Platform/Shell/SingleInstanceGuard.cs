using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;

namespace Grably.Platform.Shell;

/// <summary>
/// Keeps one Grably per user and session. The first instance owns a named mutex and listens on a named pipe; a
/// later launch sends its command line through the pipe and exits, so "open Grably again" brings up the running one.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
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
    public static bool SendToPrimary(string appId, IReadOnlyList<string> args, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(appId), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            if (PInvoke.GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid))
            {
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

    /// <summary>Stops accepting launches (call when the app starts shutting down). The mutex stays held.</summary>
    public void StopListening()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _stop.Cancel();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The listener only ends by cancellation; nothing to report on shutdown.
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

    private static string PipeName(string appId)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return $"{appId}.{UserSid()}.{process.SessionId}";
    }

    private static string UserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? Environment.UserName;
    }
}
