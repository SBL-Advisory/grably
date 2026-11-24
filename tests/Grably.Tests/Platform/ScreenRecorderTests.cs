using Grably.Core.Recording;
using Grably.Platform.Recording;

namespace Grably.Tests.Platform;

/// <summary>The whole engine on the test pattern: needs a GPU and the bundled ffmpeg (tools/fetch-ffmpeg.ps1).</summary>
[Trait("Category", "Desktop")]
public sealed class ScreenRecorderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "grably-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_seconds_of_test_pattern_make_a_finished_session_with_sixty_frames(bool nv12)
    {
        var ffmpeg = FfmpegLocator.Find();
        var options = new RecorderOptions { PatternSize = (640, 360), PreferNv12 = nv12, Ffmpeg = ffmpeg, SessionsFolder = _folder };
        var recorder = await ScreenRecorder.StartAsync(options, new EncoderProbe(ffmpeg), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TestContext.Current.CancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(2) - recorder.Elapsed, TestContext.Current.CancellationToken);
        var result = await recorder.StopAsync();

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(nv12 ? RawPixelFormat.Nv12 : RawPixelFormat.Bgra, result.Format);
        Assert.InRange(result.Frames, 60, 62);
        Assert.Equal(SessionState.Finished, RecordingSession.Load(result.SessionFolder)!.State);
        Assert.True(new FileInfo(result.VideoFile).Length > 1000);

        var mp4 = Path.Combine(_folder, "out.mp4");
        Assert.Null(await ScreenRecorder.SaveAsMp4Async(ffmpeg, result.Session, result.SessionFolder, mp4));
        Assert.True(File.Exists(mp4));
    }
}
