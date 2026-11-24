using Grably.Core.Recording;

namespace Grably.Tests.Core;

public class SessionRecoveryTests
{
    [Fact]
    public void Only_sessions_with_recorded_video_and_no_saved_mp4_are_offered_back()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grably-sessions-{Guid.NewGuid():N}");
        try
        {
            var unsaved = Session(root, "unsaved", video: [1, 2, 3], output: null);
            Session(root, "saved", video: [1, 2, 3], output: @"C:\Videos\done.mp4");
            Session(root, "nothing-recorded", video: [], output: null);
            Directory.CreateDirectory(Path.Combine(root, "no-session-file"));

            var found = RecordingSession.FindUnsaved(root);

            Assert.Equal([unsaved], found.Select(f => f.Folder));
            Assert.Equal("unsaved", found[0].Session.Id);
            Assert.Empty(RecordingSession.FindUnsaved(Path.Combine(root, "missing")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Session(string root, string id, byte[] video, string? output)
    {
        var folder = Path.Combine(root, id);
        new RecordingSession
        {
            Id = id,
            StartedUtc = DateTime.UtcNow,
            Target = "Test pattern",
            Width = 1280,
            Height = 720,
            FrameRate = 30,
            Encoder = "h264_qsv",
            OutputPath = output,
        }.Save(folder);
        File.WriteAllBytes(Path.Combine(folder, RecordingSession.VideoFileName), video);
        return folder;
    }
}
