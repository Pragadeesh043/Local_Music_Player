using NAudio.CoreAudioApi;

namespace Soundroom;

internal sealed record HardwareOutput(float? Decibels, float? Scalar, bool? Muted, float? MinimumDb,
    float? MaximumDb, float? StepDb)
{
    public static HardwareOutput Read(string id)
    {
        using var devices = new MMDeviceEnumerator();
        using var endpoint = devices.GetDevice(id);
        using var volume = endpoint.AudioEndpointVolume;
        var support = volume.HardwareSupport;
        bool hardwareVolume = support.HasFlag(EEndpointHardwareSupport.Volume);
        bool? muted = support.HasFlag(EEndpointHardwareSupport.Mute) ? volume.Mute : null;
        if (!hardwareVolume)
            return new(null, null, muted, null, null, null);
        var range = volume.VolumeRange;
        return new(volume.MasterVolumeLevel, volume.MasterVolumeLevelScalar, muted,
            range.MinDecibels, range.MaxDecibels, range.IncrementDecibels);
    }

    public static void SetMute(string id, bool mute)
    {
        using var devices = new MMDeviceEnumerator();
        using var endpoint = devices.GetDevice(id);
        using var volume = endpoint.AudioEndpointVolume;
        if (!volume.HardwareSupport.HasFlag(EEndpointHardwareSupport.Mute))
            throw new InvalidOperationException("The endpoint no longer reports a hardware mute control.");
        volume.Mute = mute;
    }
}
