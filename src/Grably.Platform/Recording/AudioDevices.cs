using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Grably.Platform.Recording;

public sealed record AudioDevice(string Id, string Name);

/// <summary>The audio devices recording can use.</summary>
public static class AudioDevices
{
    /// <summary>Active microphones and other inputs.</summary>
    public static IReadOnlyList<AudioDevice> Microphones() => List(DataFlow.Capture);

    /// <summary>Active outputs (speakers, headphones), whose sound can be recorded.</summary>
    public static IReadOnlyList<AudioDevice> Outputs() => List(DataFlow.Render);

    private static List<AudioDevice> List(DataFlow flow)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            return devices.EnumerateAudioEndPoints(flow, DeviceState.Active).Select(d => new AudioDevice(d.ID, d.FriendlyName)).ToList();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return []; // the audio service is not running
        }
    }
}

/// <summary>
/// The microphone's live level for the pre-record bar, from a short-lived capture stream (an input's peak meter
/// only moves while something is capturing). Raises <see cref="Level"/> (0 to 1) on the capture thread.
/// </summary>
public sealed class MicrophoneLevelMeter : IDisposable
{
    private readonly WasapiRecorder _recorder;

    /// <exception cref="System.Runtime.InteropServices.COMException">There is no usable microphone.</exception>
    public MicrophoneLevelMeter(string? deviceId)
    {
        using var devices = new MMDeviceEnumerator();
        var builder = new WasapiRecorderBuilder().WithSharedMode().WithFormat(AudioTrackFormat.WaveFormat);
        builder = deviceId is null ? builder.WithDefaultDeviceStreamRouting() : builder.WithDevice(devices.GetDevice(deviceId));
        _recorder = builder.Build();
        _recorder.DataAvailable += OnData;
        _recorder.StartRecording();
    }

    public event EventHandler<float>? Level;

    public void Dispose()
    {
        _recorder.DataAvailable -= OnData;
        try
        {
            _recorder.StopRecording();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Device gone.
        }

        _recorder.Dispose();
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        Level?.Invoke(this, (flags & AudioClientBufferFlags.Silent) != 0 ? 0 : Math.Min(1, peak));
    }
}
