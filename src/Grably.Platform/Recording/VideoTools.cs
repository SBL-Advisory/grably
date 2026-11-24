using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Grably.Core.Recording;
using Grably.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Grably.Platform.Recording;

/// <summary>What ffprobe says about a video.</summary>
public sealed record MediaInfo(TimeSpan Duration, int Width, int Height, double FrameRate, bool HasAudio)
{
    /// <summary>The length of one frame.</summary>
    public TimeSpan FrameLength => TimeSpan.FromSeconds(1 / Math.Max(1, FrameRate));
}

/// <summary>Reading videos for the trim editor: their details, single exact frames, and the timeline pictures.</summary>
public static class VideoTools
{
    /// <exception cref="InvalidDataException">ffprobe could not read the file.</exception>
    public static async Task<MediaInfo> ProbeAsync(string ffmpeg, string file, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(FfmpegLocator.ProbeNextTo(ffmpeg)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "stream=codec_type,width,height,avg_frame_rate:format=duration", "-of", "json", file })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidDataException("ffprobe did not start.");
        using var killOnCancel = cancellationToken.Register(() => Kill(process));
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        var json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"The video could not be read: {(await errors).Trim()}");
        }

        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            var streams = root.GetProperty("streams").EnumerateArray().ToList();
            var video = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
            var rate = video.GetProperty("avg_frame_rate").GetString()!.Split('/');
            var fps = rate.Length == 2 && double.TryParse(rate[1], CultureInfo.InvariantCulture, out var d) && d > 0
                ? double.Parse(rate[0], CultureInfo.InvariantCulture) / d
                : 30;
            return new MediaInfo(
                TimeSpan.FromSeconds(double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture)),
                video.GetProperty("width").GetInt32(),
                video.GetProperty("height").GetInt32(),
                fps,
                streams.Any(s => s.GetProperty("codec_type").GetString() == "audio"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The video's details could not be read.", ex);
        }
    }

    /// <summary>
    /// Frame number <paramref name="index"/> as BMP bytes, at most <paramref name="maxWidth"/> wide when given, or null
    /// when it cannot be decoded.
    /// </summary>
    public static async Task<byte[]?> FrameAsync(
        string ffmpeg, string video, int index, double fps, int? maxWidth = null, CancellationToken cancellationToken = default)
    {
        using var process = FfmpegProcess.Start(
            ffmpeg, FfmpegArguments.FrameAt(video, VideoFrames.SeekTime(index, fps), maxWidth), withInput: false, withOutput: true);
        using var killOnCancel = cancellationToken.Register(process.Kill);
        using var image = new MemoryStream();
        await process.Output.CopyToAsync(image, cancellationToken);
        await process.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        return image.Length > 0 && process.HasExited && process.ExitCode == 0 ? image.ToArray() : null;
    }

    /// <summary>Writes the timeline's thumbnail strip; false when it could not be made.</summary>
    public static async Task<bool> ThumbnailStripAsync(
        string ffmpeg, string video, TimeSpan duration, int count, int height, string output, CancellationToken cancellationToken = default) =>
        (await FfmpegProcess.RunAsync(ffmpeg, FfmpegArguments.ThumbnailStrip(video, duration, count, height, output), TimeSpan.FromMinutes(2), cancellationToken)).ExitCode == 0
        && File.Exists(output);

    /// <summary>Writes the timeline's waveform picture; false when it could not be made (no sound).</summary>
    public static async Task<bool> WaveformAsync(string ffmpeg, string video, int width, int height, string output, CancellationToken cancellationToken = default) =>
        (await FfmpegProcess.RunAsync(ffmpeg, FfmpegArguments.Waveform(video, width, height, output), TimeSpan.FromMinutes(2), cancellationToken)).ExitCode == 0
        && File.Exists(output);

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}

/// <summary>
/// Exports an edited video: MP4 with the encoder that works on this PC, or GIF, always re-encoded so every cut is
/// exact to the frame. Reports progress (0 to 1); cancelling stops ffmpeg and removes the unfinished file. The video
/// is written as <c>name.mp4.part</c> and renamed when complete, so a crash or sign-out never leaves a broken file
/// under the real name (a leftover part is overwritten by the next export to that name).
/// </summary>
public sealed class VideoExporter(string ffmpeg, EncoderProbe probe, ILogger logger)
{
    /// <summary>Returns null when done, or why it failed.</summary>
    /// <exception cref="OperationCanceledException">Cancelled; the unfinished file is gone.</exception>
    public async Task<string?> ExportAsync(
        string input, EditList edits, ExportSettings settings, VideoQuality quality, string output,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await VideoTools.ProbeAsync(ffmpeg, input, cancellationToken);
        var fps = (int)Math.Round(info.FrameRate);
        int width, height;
        VideoEncoder? encoder = null;
        if (settings.Format == ExportFormat.Gif)
        {
            width = Math.Min(settings.GifWidth, info.Width) & ~1;
            height = (int)Math.Round(info.Height * (double)width / info.Width) & ~1;
        }
        else
        {
            (width, height) = OutputSize.For(info.Width, info.Height, settings.MaxHeight);
            encoder = await probe.ChooseAsync(width, height, cancellationToken);
            if (encoder is null)
            {
                return "No video encoder works on this PC.";
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var partial = output + ".part";
        var arguments = FfmpegArguments.Export(input, edits, settings, info.HasAudio, encoder, quality, width, height, fps, partial);
        logger.LogInformation(
            "Exporting {Format} {Width}x{Height} of {Parts} part(s), {Duration} long, with {Encoder}",
            settings.Format, width, height, edits.Kept.Count, edits.Duration, encoder?.Name ?? "gif");
        using var process = FfmpegProcess.Start(ffmpeg, arguments, withInput: false, withOutput: true);
        // Stop ffmpeg the moment the export is cancelled, even when the app is closing and nothing awaits this any more.
        using var killOnCancel = cancellationToken.Register(process.Kill);
        try
        {
            using var lines = new StreamReader(process.Output);
            while (await lines.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var microseconds) && edits.Duration > TimeSpan.Zero)
                {
                    progress?.Report(Math.Clamp(microseconds / 1_000_000.0 / edits.Duration.TotalSeconds, 0, 1));
                }
            }

            await process.WaitAsync(TimeSpan.FromMinutes(10), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await process.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            DeleteQuietly(partial);
            throw;
        }

        if (!process.HasExited || process.ExitCode != 0)
        {
            DeleteQuietly(partial);
            logger.LogError("Export failed ({Code}): {Errors}", process.HasExited ? process.ExitCode : -1, process.Errors);
            return $"The export failed: {process.Errors.Split('\n')[0].Trim()}";
        }

        File.Move(partial, output);
        progress?.Report(1);
        return null;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still locked by a dying ffmpeg; it keeps its .part name, so it is never mistaken for an export.
        }
    }
}
