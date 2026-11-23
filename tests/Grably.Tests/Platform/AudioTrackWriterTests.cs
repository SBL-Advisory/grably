using Grably.Core.Recording;
using Grably.Platform.Recording;

namespace Grably.Tests.Platform;

public sealed class AudioTrackWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "grably-tests", Guid.NewGuid().ToString("N"));
    private long _now = 10_000_000;

    public AudioTrackWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Packets_land_on_the_clock_and_a_silent_track_is_padded_to_the_end()
    {
        var clock = new RecordingClock(() => _now);
        clock.Start();
        var start = _now;
        var writer = new AudioTrackWriter(AudioTrackKind.System, _folder, clock);
        var packet = new byte[480 * AudioTrackFormat.BytesPerFrame]; // 10 ms
        Array.Fill(packet, (byte)1);

        writer.Write(packet, silent: false, start - 1); // before the start: dropped
        writer.Write(packet, silent: false, start + (TimeSpan.TicksPerSecond / 2)); // half a second in: silence before it
        writer.Finish(TimeSpan.FromSeconds(2));

        var bytes = File.ReadAllBytes(Path.Combine(_folder, writer.FileName));
        Assert.Equal(2 * 48_000 * AudioTrackFormat.BytesPerFrame, bytes.Length);
        Assert.Equal(0, bytes[(24_000 * AudioTrackFormat.BytesPerFrame) - 1]);
        Assert.Equal(1, bytes[24_000 * AudioTrackFormat.BytesPerFrame]);
        Assert.Equal(0, bytes[^1]);
    }

    [Fact]
    public void Silence_is_filled_in_as_time_passes_and_a_packet_without_a_timestamp_ends_now()
    {
        var clock = new RecordingClock(() => _now);
        clock.Start();
        var writer = new AudioTrackWriter(AudioTrackKind.System, _folder, clock);
        _now += TimeSpan.TicksPerSecond;

        writer.PadTo(TimeSpan.FromSeconds(0.5)); // the keep-alive: half a second of silence so far
        writer.Write(new byte[480 * AudioTrackFormat.BytesPerFrame], silent: false, capturedAt: 0); // no timestamp
        writer.Dispose();

        // The packet without a timestamp is taken to have just ended: it lands at 0.99 s to 1 s.
        Assert.Equal(48_000 * AudioTrackFormat.BytesPerFrame, new FileInfo(Path.Combine(_folder, writer.FileName)).Length);
    }

    [Fact]
    public void A_muted_microphone_writes_silence_and_keeps_time()
    {
        var clock = new RecordingClock(() => _now);
        clock.Start();
        var writer = new AudioTrackWriter(AudioTrackKind.Microphone, _folder, clock) { Muted = true };
        var packet = new byte[480 * AudioTrackFormat.BytesPerFrame];
        Array.Fill(packet, (byte)7);

        writer.Write(packet, silent: false, _now);
        writer.Dispose();

        var bytes = File.ReadAllBytes(Path.Combine(_folder, writer.FileName));
        Assert.Equal(packet.Length, bytes.Length);
        Assert.All(bytes, b => Assert.Equal(0, b));
    }
}
