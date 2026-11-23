using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.Wave;

namespace Soundroom;

public sealed partial class MainWindow
{
    private (Playback Session, Track Track, ProcessingSnapshot Snapshot, bool Hardware)? signalView;
    private readonly HashSet<string> expandedSignalStages = [];
    private long nextReadback;
    private bool readingHardware;
    private bool? hardwareMute;

    private void OpenSignalInspector(object sender, RoutedEventArgs args)
    {
        InspectorTabs.SelectedItem = SignalTab;
        if (playback is { } session)
            _ = RefreshHardwareReadback(session, force: true);
    }

    private void ClearSignalPath()
    {
        if (signalView is null)
            return;
        signalView = null;
        SignalPath.Text = "";
        SignalVerdict.Text = "No active output";
        SignalVerdict.Foreground = neutral;
        SignalExplanation.Text = "Play a track to inspect its audio path.";
        SignalNotice.Visibility = Visibility.Collapsed;
        SignalStages.Children.Clear();
        HardwareReadbackPanel.Visibility = Visibility.Collapsed;
        HardwareMuteButton.Visibility = Visibility.Collapsed;
        hardwareMute = null;
        nextReadback = 0;
    }

    private void ShowSignalPath(Playback session)
    {
        var source = session.CurrentTrack;
        var snapshot = session.ReadProcessing(out bool applied, out bool pending);
        bool hardware = session.OutputRoute != Route.NativeDsd && session.Device.HardwareVolume &&
            outputProfile.VolumeMode == VolumeMode.Hardware;
        var current = (session, source, snapshot, hardware);
        if (signalView != current)
        {
            if (!ReferenceEquals(signalView?.Session, session))
            {
                nextReadback = 0;
                hardwareMute = null;
                HardwareReadbackPanel.Visibility = Visibility.Collapsed;
                HardwareMuteButton.Visibility = Visibility.Collapsed;
            }
            signalView = current;
            var format = session.WaveFormat;
            var route = new SignalRoute(session.OutputRoute, session.Device, format.SampleRate, session.OutputBits,
                format.BitsPerSample, session.SourceChannels, session.OutputChannels, session.AsioDriver,
                session.MinimumPhase, session.ImpulsePath);
            var report = SignalPathReport.Describe(source, route, snapshot, hardware);
            SignalPath.Text = $"{report.Verdict}\n{report.OutputSummary}";
            SignalVerdict.Text = report.Verdict;
            SignalVerdict.Foreground = report.ExactAtOutput ? accent : neutral;
            SignalExplanation.Text = report.Explanation;
            SignalStages.Children.Clear();
            foreach (var stage in report.Stages)
            {
                var heading = new StackPanel { Spacing = 2 };
                heading.Children.Add(new TextBlock { Text = stage.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                heading.Children.Add(new TextBlock { Text = stage.Summary, Foreground = neutral, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
                var row = new Expander
                {
                    Header = heading, IsExpanded = expandedSignalStages.Contains(stage.Name),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new TextBlock { Text = stage.Detail, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
                };
                row.Expanding += (_, _) => expandedSignalStages.Add(stage.Name);
                row.Collapsed += (_, _) => expandedSignalStages.Remove(stage.Name);
                SignalStages.Children.Add(row);
            }
        }
        string notice = !applied ? "Configured route. Waiting for the first audio block."
            : pending && session.IsProcessed ? "Showing delivered blocks. New DSP settings are still queued."
            : session.State == PlaybackState.Paused ? "Paused. Showing the last delivered audio block."
            : "";
        if (SignalNotice.Text != notice)
        {
            SignalNotice.Text = notice;
            SignalNotice.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (ReferenceEquals(InspectorTabs.SelectedItem, SignalTab))
            _ = RefreshHardwareReadback(session);
    }

    private async Task RefreshHardwareReadback(Playback session, bool force = false)
    {
        if (readingHardware || (!force && Environment.TickCount64 < nextReadback))
            return;
        readingHardware = true;
        nextReadback = Environment.TickCount64 + 1500;
        try
        {
            if (session.OutputRoute == Route.NativeDsd)
            {
                string rate = session.ReadAsioSampleRate() is double value ? $"{value:N0} Hz" : "Unavailable";
                HardwareReadbackText.Text = $"ASIO driver-reported sample rate · {rate}";
                hardwareMute = null;
                HardwareMuteButton.Visibility = Visibility.Collapsed;
                HardwareReadbackPanel.Visibility = Visibility.Visible;
                return;
            }

            var state = await Task.Run(() => HardwareOutput.Read(session.Device.Id));
            if (!ReferenceEquals(playback, session))
                return;
            var rows = new List<string> { "Physical clock · unavailable through this Windows endpoint" };
            if (state.Decibels is float db)
                rows.Add($"Hardware volume · {db:0.0} dB ({state.Scalar:P0})\nRange {state.MinimumDb:0.#} to {state.MaximumDb:0.#} dB · step {state.StepDb:0.##} dB");
            else
                rows.Add("Hardware volume · unsupported by this endpoint");
            hardwareMute = state.Muted;
            HardwareMuteButton.Visibility = hardwareMute.HasValue ? Visibility.Visible : Visibility.Collapsed;
            HardwareMuteButton.Content = hardwareMute == true ? "Unmute hardware" : "Mute hardware";
            HardwareReadbackText.Text = string.Join("\n", rows);
            HardwareReadbackPanel.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            if (!ReferenceEquals(playback, session))
                return;
            HardwareReadbackText.Text = "Hardware readback is unavailable.";
            HardwareReadbackPanel.Visibility = Visibility.Visible;
            HardwareMuteButton.Visibility = Visibility.Collapsed;
            hardwareMute = null;
        }
        finally { readingHardware = false; }
    }

    private async void ToggleHardwareMute(object sender, RoutedEventArgs args)
    {
        if (playback is not { } session || hardwareMute is not bool muted || session.OutputRoute == Route.NativeDsd)
            return;
        HardwareMuteButton.IsEnabled = false;
        try
        {
            HardwareOutput.SetMute(session.Device.Id, !muted);
            await RefreshHardwareReadback(session, force: true);
        }
        catch (Exception error) { ShowError("Setting hardware mute", error); }
        finally { HardwareMuteButton.IsEnabled = true; }
    }
}
