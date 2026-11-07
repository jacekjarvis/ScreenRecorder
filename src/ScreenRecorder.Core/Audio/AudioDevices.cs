using NAudio.CoreAudioApi;

namespace ScreenRecorder.Core.Audio;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

public static class AudioDevices
{
    /// <summary>Speakers / headphones — recorded as "PC sounds".</summary>
    public static IReadOnlyList<AudioDevice> GetOutputs() => List(DataFlow.Render);

    /// <summary>Microphones and other inputs.</summary>
    public static IReadOnlyList<AudioDevice> GetInputs() => List(DataFlow.Capture);

    private static IReadOnlyList<AudioDevice> List(DataFlow flow)
    {
        using var e = new MMDeviceEnumerator();
        string? defaultId = null;
        try { defaultId = e.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID; } catch { }
        var list = new List<AudioDevice>();
        foreach (var d in e.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            list.Add(new AudioDevice(d.ID, d.FriendlyName, d.ID == defaultId));
            d.Dispose();
        }
        return list.OrderByDescending(d => d.IsDefault).ToList();
    }

    /// <summary>Opens the device with this id, or the default one when the id is empty or gone.</summary>
    internal static MMDevice Open(string? id, bool output)
    {
        using var e = new MMDeviceEnumerator();
        if (!string.IsNullOrEmpty(id))
        {
            try { return e.GetDevice(id); } catch { }
        }
        return e.GetDefaultAudioEndpoint(output ? DataFlow.Render : DataFlow.Capture, Role.Multimedia);
    }
}
