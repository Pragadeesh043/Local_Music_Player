using Microsoft.UI.Xaml;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundroom;

internal enum VolumeMode { Digital, Fixed, Hardware }
internal sealed record OutputProfile(Route Route, VolumeMode VolumeMode = VolumeMode.Digital, double Volume = 75,
    bool Memory = false, int TargetRate = 0, int OutputBits = 24, bool Sharper = false,
    bool DopConfirmed = false, DspSettings? Dsp = null, bool MinimumPhase = false, string? AsioDriver = null,
    int MemoryLimitMiB = RamPlaybackBudget.DefaultLimitMiB);

public sealed partial class MainWindow
{
    private static readonly int[] OutputRates = [0, 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000];
    private OutputProfile outputProfile = new(Route.Binaural);
    private List<OutputDevice> outputDevices = [];
    private OutputDevice? outputDevice;
    private bool followDefault = true;
    private bool refreshingDevices;
    private double outputVolume = 75;
    private bool volumeEnabled;
    private readonly DispatcherTimer deviceTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int generation;

    private Route SelectedRoute => outputProfile.Route;
    private static Route DefaultRoute(OutputDevice device) => device.Channels == 8 &&
        device.Name.Contains("Cable", StringComparison.OrdinalIgnoreCase) ? Route.Cable : Route.Binaural;

    private void InitializeOutput()
    {
        try
        {
            outputDevices = Playback.Devices();
            var preferred = library.Get<string>("preferred-output");
            var chosen = outputDevices.FirstOrDefault(device => device.Id == preferred);
            followDefault = chosen is null;
            LoadOutputProfile(chosen ?? outputDevices.FirstOrDefault());
        }
        catch (Exception error) { status = Diagnostics.Report("Detecting audio outputs", error); }
        if (outputDevice is null && status.Length == 0)
            status = "Windows reports no active audio output.";
        deviceTimer.Tick += (_, _) => RefreshDevices();
        deviceTimer.Start();
    }

    private void LoadOutputProfile(OutputDevice? device)
    {
        outputDevice = device;
        if (device is not null)
        {
            var saved = library.Get<OutputProfile>("output:" + device.Id);
            outputProfile = saved is not null && Enum.IsDefined(saved.VolumeMode) && saved.Volume is >= 0 and <= 100 &&
                saved.OutputBits is 16 or 24 or 32 && saved.TargetRate >= 0
                ? saved : new(DefaultRoute(device));
            if (!Enum.IsDefined(outputProfile.Route))
                outputProfile = outputProfile with { Route = DefaultRoute(device) };
            dsp = outputProfile.Dsp is { Parametric: not null, Graphic.Length: 10 } preset ? preset : DspSettings.Flat();
            outputVolume = outputProfile.Volume;
        }
        RefreshVolumeMode();
    }

    private async void RefreshDevices()
    {
        if (refreshingDevices || !ready)
            return;
        refreshingDevices = true;
        try
        {
            var list = await Task.Run(Playback.Devices);
            if (!ready || outputDevices.SequenceEqual(list))
                return;
            var prior = outputDevice;
            var selected = !followDefault && prior is not null ? list.FirstOrDefault(device => device.Id == prior.Id) : null;
            selected ??= list.FirstOrDefault();
            outputDevices = list;
            if (prior?.Id != selected?.Id)
                LoadOutputProfile(selected);
            else
                outputDevice = selected;
            if (track is not null && prior != selected)
                await RestartPlaybackAsync();
        }
        catch (Exception error) { Diagnostics.Write("Refreshing audio devices", error); }
        finally { refreshingDevices = false; }
    }

    private float DigitalVolume() => outputProfile.VolumeMode == VolumeMode.Digital ? (float)(outputVolume / 100) : 1;
    private bool HardwareVolume => SelectedRoute != Route.NativeDsd && outputProfile.VolumeMode == VolumeMode.Hardware &&
        outputDevice is { HardwareVolume: true };

    private void RefreshVolumeMode()
    {
        volumeEnabled = outputDevice is not null && (HardwareVolume ||
            (SelectedRoute is Route.Binaural or Route.Downmix or Route.ExclusiveDsp && outputProfile.VolumeMode == VolumeMode.Digital));
        if (HardwareVolume && outputDevice is { } device)
        {
            try
            {
                using var devices = new MMDeviceEnumerator();
                using var endpoint = devices.GetDevice(device.Id);
                using var level = endpoint.AudioEndpointVolume;
                outputVolume = level.MasterVolumeLevelScalar * 100;
            }
            catch (Exception error) { ShowError("Reading DAC volume", error); }
        }
    }

    private void UpdateOutputVolume(double value)
    {
        if (!volumeEnabled)
            throw new InvalidOperationException("This output uses fixed or external volume.");
        if (HardwareVolume && outputDevice is { } device)
        {
            using var devices = new MMDeviceEnumerator();
            using var endpoint = devices.GetDevice(device.Id);
            using var level = endpoint.AudioEndpointVolume;
            level.MasterVolumeLevelScalar = (float)(value / 100);
        }
        outputVolume = value;
        playback?.Update(dsp, DigitalVolume());
    }

    private async Task<object> ReadOutput()
    {
        string[] drivers = await Task.Run(AsioOut.GetDriverNames);
        return new
        {
            devices = outputDevices, selected = outputDevice?.Id, followDefault, drivers, rates = OutputRates,
            minimumMemory = RamPlaybackBudget.MinimumLimitMiB, maximumMemory = RamPlaybackBudget.MaximumLimitMiB,
            profile = new { outputProfile.Route, outputProfile.VolumeMode, outputProfile.Memory, outputProfile.MemoryLimitMiB,
                outputProfile.TargetRate, outputProfile.OutputBits, outputProfile.Sharper, outputProfile.DopConfirmed,
                outputProfile.MinimumPhase, outputProfile.AsioDriver }
        };
    }

    private async Task SelectOutput(string id)
    {
        var selected = id.Length == 0 ? outputDevices.FirstOrDefault() : outputDevices.FirstOrDefault(device => device.Id == id);
        if (selected is null)
            throw new InvalidOperationException("This output device is unavailable.");
        bool changed = outputDevice?.Id != selected.Id;
        followDefault = id.Length == 0;
        library.Put("preferred-output", id);
        if (changed)
        {
            LoadOutputProfile(selected);
            await RestartPlaybackAsync();
        }
    }

    private async Task SaveOutput(string id, OutputProfile settings)
    {
        if (outputDevice is not { } device || device.Id != id)
            throw new InvalidOperationException("The audio device changed. Reopen Output settings to reload its profile.");
        if (!Enum.IsDefined(settings.Route) || !Enum.IsDefined(settings.VolumeMode) ||
            (settings.VolumeMode == VolumeMode.Hardware && !device.HardwareVolume) ||
            !OutputRates.Contains(settings.TargetRate) || settings.OutputBits is not (16 or 24 or 32) ||
            settings.MemoryLimitMiB < RamPlaybackBudget.MinimumLimitMiB ||
            settings.MemoryLimitMiB > RamPlaybackBudget.MaximumLimitMiB)
            throw new InvalidOperationException("The output profile contains an unsupported setting.");
        if (settings.Route == Route.Dop && !settings.DopConfirmed)
            throw new InvalidOperationException("Confirm that the DAC supports DoP before saving this route.");
        if (settings.Route == Route.NativeDsd && string.IsNullOrEmpty(settings.AsioDriver))
            throw new InvalidOperationException("Select an installed ASIO driver for native DSD.");
        if (settings.AsioDriver is { } driver && !(await Task.Run(AsioOut.GetDriverNames)).Contains(driver))
            throw new InvalidOperationException("The selected ASIO driver is unavailable.");
        if (!ready || outputDevice?.Id != id)
            throw new InvalidOperationException("The audio device changed. Reopen Output settings to reload its profile.");
        var updated = settings with { Volume = outputVolume, Dsp = dsp };
        library.Put("output:" + device.Id, updated);
        outputProfile = updated;
        RefreshVolumeMode();
        await RestartPlaybackAsync();
    }
}
