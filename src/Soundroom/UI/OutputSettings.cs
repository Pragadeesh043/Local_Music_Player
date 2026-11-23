using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private OutputProfile outputProfile = new(Route.Binaural);
    private bool changingDevice, refreshingDevices;
    private readonly DispatcherTimer deviceTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int generation;

    private static Route DefaultRoute(OutputDevice device) => device.Channels == 8 &&
        device.Name.Contains("Cable", StringComparison.OrdinalIgnoreCase) ? Route.Cable : Route.Binaural;

    private void LoadOutputProfile()
    {
        if (Devices.SelectedItem is not OutputDevice device)
            return;
        bool prior = changingDevice;
        changingDevice = true;
        try
        {
            var saved = library.Get<OutputProfile>("output:" + device.Id);
            outputProfile = saved is not null && Enum.IsDefined(saved.Route) &&
                Enum.IsDefined(saved.VolumeMode) && saved.Volume is >= 0 and <= 100 &&
                saved.OutputBits is 16 or 24 or 32 && saved.TargetRate >= 0
                ? saved : new(DefaultRoute(device));
            Routes.SelectedIndex = (int)outputProfile.Route;
            LoadDsp(outputProfile.Dsp is { Parametric: not null, Graphic.Length: 10 } dspProfile
                ? dspProfile : DspSettings.Flat());
            Volume.Value = outputProfile.Volume;
            RefreshVolumeMode();
        }
        finally { changingDevice = prior; }
    }

    private async void RefreshDevices()
    {
        if (refreshingDevices || !ready)
            return;
        refreshingDevices = true;
        try
        {
            var list = await Task.Run(Playback.Devices);
            if (!ready || Devices.ItemsSource is not List<OutputDevice> previous || previous.SequenceEqual(list))
                return;
            var prior = Devices.SelectedItem as OutputDevice;
            var selected = prior is not null && prior.Id != previous.FirstOrDefault()?.Id
                ? list.FirstOrDefault(device => device.Id == prior.Id) ?? list.FirstOrDefault()
                : list.FirstOrDefault();
            changingDevice = true;
            Devices.ItemsSource = list;
            Devices.SelectedItem = selected;
            if (prior?.Id != selected?.Id)
                LoadOutputProfile();
            changingDevice = false;
            if (track is not null && prior != selected)
                Start(playback?.Position ?? Seek.Value, playback?.State == PlaybackState.Playing);
        }
        catch (Exception error) { Diagnostics.Write("Refreshing audio devices", error); }
        finally { refreshingDevices = false; changingDevice = false; }
    }

    private float DigitalVolume() => outputProfile.VolumeMode == VolumeMode.Digital ? (float)(Volume.Value / 100) : 1;
    private bool CanAdjustHardware => Devices.SelectedItem is OutputDevice { HardwareVolume: true };

    private void RefreshVolumeMode()
    {
        bool availableHardware = SelectedRoute != Route.NativeDsd && outputProfile.VolumeMode == VolumeMode.Hardware && CanAdjustHardware;
        Volume.IsEnabled = availableHardware || (SelectedRoute is Route.Binaural or Route.Downmix or Route.ExclusiveDsp &&
            outputProfile.VolumeMode == VolumeMode.Digital);
        Volume.Header = availableHardware ? "DAC volume" : Volume.IsEnabled ? "Volume" : "Fixed / external";
        if (availableHardware && Devices.SelectedItem is OutputDevice device)
        {
            try
            {
                using var devices = new MMDeviceEnumerator();
                using var endpoint = devices.GetDevice(device.Id);
                using var level = endpoint.AudioEndpointVolume;
                bool prior = changingDevice;
                changingDevice = true;
                Volume.Value = level.MasterVolumeLevelScalar * 100;
                changingDevice = prior;
            }
            catch (Exception error) { ShowError("Reading DAC volume", error); }
        }
    }

    private void UpdateOutputVolume()
    {
        if (SelectedRoute != Route.NativeDsd && outputProfile.VolumeMode == VolumeMode.Hardware && CanAdjustHardware && Devices.SelectedItem is OutputDevice device)
        {
            try
            {
                using var devices = new MMDeviceEnumerator();
                using var endpoint = devices.GetDevice(device.Id);
                using var level = endpoint.AudioEndpointVolume;
                level.MasterVolumeLevelScalar = (float)(Volume.Value / 100);
            }
            catch (Exception error) { ShowError("Setting DAC volume", error); }
        }
        playback?.Update(dsp, DigitalVolume());
        if (playback is not null)
            ShowSignalPath(playback);
    }

    private async void ShowOutputSettings(object sender, RoutedEventArgs args) =>
        await RunUiAction("Updating the output profile", ShowOutputSettingsAsync);

    private async Task ShowOutputSettingsAsync()
    {
        if (Devices.SelectedItem is not OutputDevice device)
            return;
        var controls = new StackPanel { Spacing = 10 };
        var memory = new CheckBox { Content = "Decode tracks to RAM", IsChecked = outputProfile.Memory };
        var memoryLimit = new NumberBox
        {
            Header = "Total RAM playback limit (MiB)", Value = Math.Clamp(outputProfile.MemoryLimitMiB,
                RamPlaybackBudget.MinimumLimitMiB, RamPlaybackBudget.MaximumLimitMiB),
            Minimum = RamPlaybackBudget.MinimumLimitMiB, Maximum = RamPlaybackBudget.MaximumLimitMiB, SmallChange = 16,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, IsEnabled = outputProfile.Memory
        };
        ToolTipService.SetToolTip(memoryLimit, "The current and next track share this limit. A track streams from its source if it does not fit.");
        memory.Checked += (_, _) => memoryLimit.IsEnabled = true;
        memory.Unchecked += (_, _) => memoryLimit.IsEnabled = false;
        var sharper = new CheckBox { Content = "Steeper resampling filter", IsChecked = outputProfile.Sharper };
        var phase = new ComboBox { Header = "Resampler phase", ItemsSource = new[] { "Linear", "Minimum" },
            SelectedIndex = outputProfile.MinimumPhase ? 1 : 0 };
        string[] drivers = AsioOut.GetDriverNames().Prepend("Choose a driver").ToArray();
        var asio = new ComboBox { Header = "Native DSD ASIO driver", ItemsSource = drivers,
            SelectedIndex = Math.Max(0, Array.IndexOf(drivers, outputProfile.AsioDriver)) };
        var dop = new CheckBox { Content = "This endpoint is connected to a DoP-capable DAC", IsChecked = outputProfile.DopConfirmed };
        var modes = new ComboBox { Header = "Volume mode", ItemsSource = device.HardwareVolume
            ? new[] { "Digital volume", "Fixed line-out", "DAC hardware volume" } : new[] { "Digital volume", "Fixed line-out" },
            SelectedIndex = Math.Min((int)outputProfile.VolumeMode, device.HardwareVolume ? 2 : 1) };
        int[] values = [0, 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000];
        var rates = new ComboBox { Header = "Exclusive DSP sample rate", ItemsSource = values.Select(value =>
            value == 0 ? "Follow track" : $"{value:N0} Hz").ToArray(), SelectedIndex = Math.Max(0, Array.IndexOf(values, outputProfile.TargetRate)) };
        int[] depths = [16, 24, 32];
        var bits = new ComboBox { Header = "Exclusive DSP PCM depth", ItemsSource = depths.Select(value => $"{value} bit").ToArray(),
            SelectedIndex = Math.Max(0, Array.IndexOf(depths, outputProfile.OutputBits)) };
        foreach (var control in new FrameworkElement[] { modes, rates, bits, phase, memory, memoryLimit, sharper, dop, asio })
            controls.Children.Add(control);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Output profile", Content = new ScrollViewer { Content = controls },
            PrimaryButtonText = "Save for this device", CloseButtonText = "Cancel" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || (Devices.SelectedItem as OutputDevice)?.Id != device.Id)
            return;
        var updated = new OutputProfile(SelectedRoute, (VolumeMode)modes.SelectedIndex, Volume.Value, memory.IsChecked == true,
            values[rates.SelectedIndex], depths[bits.SelectedIndex], sharper.IsChecked == true,
            dop.IsChecked == true, dsp, phase.SelectedIndex == 1, asio.SelectedIndex > 0 ? (string)asio.SelectedItem : null,
            (int)Math.Round(Value(memoryLimit, RamPlaybackBudget.DefaultLimitMiB)));
        library.Put("output:" + device.Id, updated);
        outputProfile = updated;
        RefreshVolumeMode();
        if (track is not null)
            Start(playback?.Position ?? Seek.Value, playback?.State == PlaybackState.Playing);
    }
}
