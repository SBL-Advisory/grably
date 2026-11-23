using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Grably.Core.Recording;
using Microsoft.Extensions.Logging;

namespace Grably.Platform.Recording;

/// <summary>Finds ffmpeg. Grably uses the LGPL build it bundles; anything else must be asked for explicitly.</summary>
public static class FfmpegLocator
{
    public const string AllowPathVariable = "GRABLY_FFMPEG_ALLOW_PATH";

    /// <summary>
    /// The bundled <c>ffmpeg\ffmpeg.exe</c> next to the app, else <paramref name="overridePath"/>, else ffmpeg on PATH
    /// only when GRABLY_FFMPEG_ALLOW_PATH=1 (a PATH build may be GPL with encoders users will not have).
    /// </summary>
    /// <exception cref="FileNotFoundException">No usable ffmpeg.</exception>
    public static string Find(string? overridePath = null)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        if (overridePath is not null && File.Exists(overridePath))
        {
            return overridePath;
        }

        if (Environment.GetEnvironmentVariable(AllowPathVariable) == "1")
        {
            var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(folder => Path.Combine(folder, "ffmpeg.exe"))
                .FirstOrDefault(File.Exists);
            if (onPath is not null)
            {
                return onPath;
            }
        }

        throw new FileNotFoundException(
            $"ffmpeg is missing from {Path.GetDirectoryName(bundled)}. Developers: run tools/fetch-ffmpeg.ps1, then build again.", bundled);
    }

    /// <summary>ffprobe beside the given ffmpeg.</summary>
    public static string ProbeNextTo(string ffmpeg) => Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");

    /// <summary>A media file's length from ffprobe, or null when it cannot be read.</summary>
    public static async Task<TimeSpan?> DurationAsync(string ffmpeg, string file)
    {
        var info = new ProcessStartInfo(ProbeNextTo(ffmpeg)) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);
        if (process is null)
        {
            return null;
        }

        var text = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}

/// <summary>
/// One ffmpeg process. Its error output is always drained on another thread (a full pipe would stall ffmpeg) and
/// the last lines kept for the log. Arguments are passed as a list, never joined by hand.
/// </summary>
public sealed class FfmpegProcess : IDisposable
{
    private const int KeptLines = 40;

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _errors = new();

    private FfmpegProcess(Process process)
    {
        _process = process;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line)
            {
                _errors.Enqueue(line);
                while (_errors.Count > KeptLines && _errors.TryDequeue(out string? _))
                {
                }
            }
        };
        _process.BeginErrorReadLine();
    }

    /// <summary>Where raw frames go (only when started with stdin).</summary>
    public Stream Input => _process.StandardInput.BaseStream;

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    /// <summary>The last lines ffmpeg wrote to its error output.</summary>
    public string Errors => string.Join(Environment.NewLine, _errors);

    public static FfmpegProcess Start(string exe, IReadOnlyList<string> arguments, bool withInput)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = withInput,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"{exe} did not start.");
        return new FfmpegProcess(process);
    }

    /// <summary>Runs ffmpeg (or ffprobe) to the end. Kills it after <paramref name="timeout"/>.</summary>
    public static async Task<(int ExitCode, string Errors)> RunAsync(
        string exe, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var process = Start(exe, arguments, withInput: false);
        var finished = await process.WaitAsync(timeout, cancellationToken);
        return finished ? (process.ExitCode, process.Errors) : (-1, "Timed out. " + process.Errors);
    }

    /// <summary>
    /// Waits for ffmpeg to exit; kills it and returns false after <paramref name="timeout"/>. If the caller cancels,
    /// ffmpeg is killed and the cancellation is passed on.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(limit.Token);
            _process.WaitForExit(); // let the error reader finish
            return true;
        }
        catch (OperationCanceledException)
        {
            Kill();
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}

/// <summary>
/// Picks the encoder for a recording by encoding a few real frames at the actual size with each candidate in turn
/// (a listed encoder proves nothing: the driver or GPU may be missing). Results are remembered per size.
/// </summary>
public sealed class EncoderProbe(string ffmpeg, ILogger? logger = null)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<(int, int), VideoEncoder?> _chosen = new();

    /// <summary>The first candidate that encodes at this size, or null when none does.</summary>
    public async Task<VideoEncoder?> ChooseAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        if (_chosen.TryGetValue((width, height), out var known))
        {
            return known;
        }

        // An encoder that works at one ordinary size works at another; only very large frames (over 4096 pixels,
        // where NVENC H.264 stops) need their own test.
        if (width <= 4096 && height <= 4096
            && _chosen.FirstOrDefault(c => c.Value is not null && c.Key.Item1 <= 4096 && c.Key.Item2 <= 4096).Value is { } usual)
        {
            return usual;
        }

        var timedOut = false;
        foreach (var encoder in VideoEncoders.Candidates)
        {
            var (code, errors) = await FfmpegProcess.RunAsync(ffmpeg, FfmpegArguments.Probe(encoder, width, height), Limit, cancellationToken);
            if (code == 0)
            {
                logger?.LogInformation("Encoder for {Width}x{Height}: {Encoder}", width, height, encoder.Name);
                _chosen[(width, height)] = encoder;
                return encoder;
            }

            timedOut |= code == -1;
            logger?.LogDebug("{Encoder} cannot encode {Width}x{Height}: {Error}", encoder.Name, width, height, FirstLine(errors));
        }

        if (!timedOut)
        {
            _chosen[(width, height)] = null; // a definite answer; a slow machine gets another try next time
        }

        return null;
    }

    /// <summary>Every candidate's result at this size (for the dev harness).</summary>
    public async Task<IReadOnlyList<(VideoEncoder Encoder, bool Works, string Error)>> TestAllAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        var results = new List<(VideoEncoder, bool, string)>();
        foreach (var encoder in VideoEncoders.Candidates)
        {
            var (code, errors) = await FfmpegProcess.RunAsync(ffmpeg, FfmpegArguments.Probe(encoder, width, height), Limit, cancellationToken);
            results.Add((encoder, code == 0, code == 0 ? "" : FirstLine(errors)));
        }

        return results;
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();
}
