using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NAudio.Dsp;
using Windows.Foundation;

namespace Soundroom;

public sealed partial class MainWindow
{
    private readonly Polyline waveformLine = new() { StrokeThickness = 1.5 };
    private readonly Polyline spectrumLine = new() { StrokeThickness = 1.5 };
    private readonly Polyline phaseLine = new() { StrokeThickness = 1.1 };
    private readonly Line phaseHorizontal = new(), phaseVertical = new();
    private readonly Polyline eqLine = new() { StrokeThickness = 2 };
    private readonly Polyline rightEqLine = new() { StrokeThickness = 1.5 };
    private readonly Complex[] fft = new Complex[Binaural.Block];
    private readonly StackPanel bandPanel = new() { Spacing = 10 };
    private readonly List<BandControls> eqBands = [];
    private Slider balance = null!, headroom = null!;
    private CheckBox invert = null!, limiter = null!;
    private ComboBox dither = null!, loudness = null!;
    private Button convolution = null!;
    private sealed record BandControls(NumberBox Frequency, NumberBox Gain, NumberBox Q,
        ComboBox Shape, ComboBox Channel, CheckBox Active);

    private void MapResized(object sender, SizeChangedEventArgs args) => DrawMap();

    private void DrawMap()
    {
        SpeakerMap.Children.Clear();
        speakers.Clear();
        double w = SpeakerMap.ActualWidth, h = SpeakerMap.ActualHeight;
        if (w < 50 || h < 50)
            return;
        var listener = new FontIcon { Glyph = "\uE7F6", FontSize = 26, Foreground = neutral };
        SpeakerMap.Children.Add(listener);
        Canvas.SetLeft(listener, w / 2 - 13);
        Canvas.SetTop(listener, h / 2 - 13);
        for (int i = 0; i < channelLabels.Length; i++)
        {
            int index = i;
            string label = channelLabels[i];
            (double x, double y) = label switch
            {
                "FL" => (.2, .19), "FR" => (.8, .19), "FC" => (.5, .17), "LFE" => (.5, .82),
                "SL" => (.13, .5), "SR" => (.87, .5),
                "BL" => (.23, .81), "BR" => (.77, .81),
                _ => (.12 + .25 * (i % 4), i < 4 ? .2 : .8)
            };
            var button = new Button { Content = label, MinWidth = 37, Padding = new Thickness(5, 2, 5, 2),
                Foreground = i == channel ? accent : neutral };
            button.Click += (_, _) => InspectChannel(index);
            speakers.Add(button);
            if (channelLabels.Length > 8)
                continue;
            SpeakerMap.Children.Add(button);
            Canvas.SetLeft(button, w * x - 19);
            Canvas.SetTop(button, h * y - 14);
        }
    }

    private void InspectChannel(int index)
    {
        channel = index;
        playback?.Inspect(index);
        InspectLabel.Text = channelLabels[index];
    }

    private void SetMeters(string[] source, string[] output)
    {
        channelLabels = source;
        SourceMeters.Children.Clear();
        OutputMeters.Children.Clear();
        sourceBars.Clear();
        outputBars.Clear();
        for (int i = 0; i < source.Length; i++)
        {
            int index = i;
            AddMeter(SourceMeters, sourceBars, source[i], () => InspectChannel(index));
        }
        foreach (string label in output)
            AddMeter(OutputMeters, outputBars, label);
        DrawMap();
        waveformLine.Points.Clear();
        spectrumLine.Points.Clear();
        InspectLabel.Text = source.Length > 0 ? source[Math.Clamp(channel, 0, source.Length - 1)] : "";
        LiveStats.Text = "";
        PhaseScopePanel.Visibility = Visibility.Collapsed;
        phaseLine.Points.Clear();
    }

    private void AddMeter(StackPanel panel, List<ProgressBar> meters, string name, Action? select = null)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        FrameworkElement label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        if (select is not null)
        {
            var button = new Button { Content = name, Padding = new Thickness(4, 1, 4, 1), FontSize = 11 };
            button.Click += (_, _) => select();
            label = button;
        }
        var level = new ProgressBar { Minimum = 0, Maximum = 60, Foreground = accent, Height = 5,
            VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(level, 1);
        row.Children.Add(label);
        row.Children.Add(level);
        panel.Children.Add(row);
        meters.Add(level);
    }

    private static double Db(float amplitude) => 20 * Math.Log10(Math.Max(amplitude, .000001f));

    private void UpdateMeters(List<ProgressBar> bars, Meter[] values)
    {
        for (int i = 0; i < values.Length && i < bars.Count; i++)
        {
            double next = Math.Clamp(Db(values[i].Peak) + 60, 0, 60);
            bars[i].Value = Math.Max(next, bars[i].Value - 3);
            bars[i].Foreground = values[i].Peak > 1 ? accent : neutral;
        }
    }

    private void DrawSignal(Signal signal)
    {
        DrawPhaseScope(signal.Phase);
        UpdateMeters(sourceBars, signal.Source);
        UpdateMeters(outputBars, signal.Output);
        for (int i = 0; i < speakers.Count && i < signal.Source.Length; i++)
            speakers[i].Foreground = signal.Source[i].Peak > .001f || i == channel ? accent : neutral;
        int index = Math.Clamp(channel, 0, signal.Source.Length - 1);
        var meter = signal.Source[index];
        string clipping = signal.Output.Any(level => level.Clips > 0)
            ? $" · Output overs {signal.Output.Sum(level => level.Clips)}" : "";
        LiveStats.Text = signal.TruePeak is double peak
            ? $"{Db((float)peak):0.0} dBTP · {signal.Lufs:0.0} LUFS-M" +
              (signal.Correlation is double phase ? $" · L/R phase {phase:+0.00;-0.00;0.00}" : "") +
              (peak > 1 ? " · Peak above full scale" : "")
            : "";
        InspectLabel.Text = $"{channelLabels[index]} · Peak {Db(meter.Peak):0.#} dBFS · " +
            $"RMS {Db(meter.Rms):0.#} dBFS · Overs {meter.Clips}{clipping}";
        if (signal.WaveChannel != channel || signal.Waveform.Length == 0)
            return;
        waveformLine.Points = Points(96, WaveCanvas, n =>
        {
            int i = n * (signal.Waveform.Length - 1) / 95;
            return .5 - Math.Clamp(signal.Waveform[i], -1, 1) * .46;
        });
        for (int i = 0; i < fft.Length; i++)
        {
            fft[i].X = i < signal.Waveform.Length
                ? signal.Waveform[i] * (float)FastFourierTransform.HannWindow(i, fft.Length) : 0;
            fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, 9, fft);
        spectrumLine.Points = Points(96, SpectrumCanvas, n =>
        {
            int bin = (int)Math.Pow(255, n / 95.0);
            return 1 - Math.Clamp((Db(MathF.Sqrt(fft[bin].X * fft[bin].X + fft[bin].Y * fft[bin].Y)) + 90) / 90, 0, 1);
        });
    }

    private void DrawPhaseScope(PhasePoint[]? data)
    {
        PhaseScopePanel.Visibility = data is null ? Visibility.Collapsed : Visibility.Visible;
        if (data is null)
            return;
        double x = PhaseScope.ActualWidth / 2, y = PhaseScope.ActualHeight / 2;
        double scale = Math.Min(x, y) * .94;
        phaseHorizontal.X1 = x - scale; phaseHorizontal.X2 = x + scale;
        phaseHorizontal.Y1 = phaseHorizontal.Y2 = y;
        phaseVertical.X1 = phaseVertical.X2 = x;
        phaseVertical.Y1 = y - scale; phaseVertical.Y2 = y + scale;
        var points = new PointCollection();
        foreach (var point in data)
            points.Add(new Point(x + scale * Math.Clamp(point.Side, -1, 1), y - scale * Math.Clamp(point.Mid, -1, 1)));
        phaseLine.Points = points;
    }

    private static PointCollection Points(int count, Canvas canvas, Func<int, double> y)
    {
        var points = new PointCollection();
        for (int i = 0; i < count; i++)
            points.Add(new Point(canvas.ActualWidth * i / (count - 1), canvas.ActualHeight * y(i)));
        return points;
    }

    private bool dspControlsBuilt;

    private void BuildAnalyzerCharts()
    {
        waveformLine.Stroke = accent;
        spectrumLine.Stroke = accent;
        phaseLine.Stroke = accent;
        phaseHorizontal.Stroke = phaseVertical.Stroke = neutral;
        phaseHorizontal.Opacity = phaseVertical.Opacity = .35;
        PhaseScope.Children.Add(phaseHorizontal);
        PhaseScope.Children.Add(phaseVertical);
        PhaseScope.Children.Add(phaseLine);
        WaveCanvas.Children.Add(waveformLine);
        SpectrumCanvas.Children.Add(spectrumLine);
        ToolTipService.SetToolTip(WaveCanvas, "Source waveform");
        ToolTipService.SetToolTip(SpectrumCanvas, "Source spectrum");
    }

    private void BuildDsp()
    {
        updatingDsp = true;
        eqLine.Stroke = accent;
        rightEqLine.Stroke = neutral;
        EqChart.Children.Add(eqLine);
        EqChart.Children.Add(rightEqLine);
        preamp = AddSlider(DspPanel, "Preamp (dB)", -24, 6, 0);
        bass = AddSlider(DspPanel, "Bass shelf (dB)", -12, 12, 0);
        width = AddSlider(DspPanel, "Stereo width (%)", 0, 200, 100);
        crossfeed = AddSlider(DspPanel, "Crossfeed (%)", 0, 100, 0);
        balance = AddSlider(DspPanel, "Balance (%)", -100, 100, 0);
        invert = AddCheck(DspPanel, "Invert polarity (180°)");
        var peq = new StackPanel { Spacing = 8 };
        var bandActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var add = new Button { Content = "Add band" };
        add.Click += (_, _) =>
        {
            if (eqBands.Count < 64)
            {
                AddBand(new EqBand(1000, 0));
                UpdateDsp();
            }
        };
        var import = new Button { Content = "Import AutoEQ" };
        import.Click += ImportAutoEq;
        bandActions.Children.Add(add);
        bandActions.Children.Add(import);
        peq.Children.Add(bandActions);
        peq.Children.Add(bandPanel);
        DspPanel.Children.Add(new Expander { Header = "Parametric EQ · up to 64 bands", Content = peq,
            HorizontalAlignment = HorizontalAlignment.Stretch });
        var graphicPanel = new StackPanel { Spacing = 5 };
        for (int i = 0; i < graphics.Length; i++)
            graphics[i] = AddSlider(graphicPanel, $"{DspSettings.Centers[i]} Hz (dB)", -12, 12, 0);
        DspPanel.Children.Add(new Expander { Header = "10-band graphic EQ", Content = graphicPanel, HorizontalAlignment = HorizontalAlignment.Stretch });
        var shaping = new StackPanel { Spacing = 8 };
        loudness = AddChoice(shaping, "Normalization", new[] { "Off", "ReplayGain track", "ReplayGain album", "R128 track", "R128 album" });
        dither = AddChoice(shaping, "Dither · exclusive DSP", new[] { "Off", "TPDF", "Noise-shaped" });
        headroom = AddSlider(shaping, "Headroom (dB)", 0, 12, 1);
        limiter = AddCheck(shaping, "Enable stereo peak limiter");
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        convolution = new Button { Content = "Load FIR WAV", MaxWidth = 220 };
        convolution.Click += LoadImpulse;
        var clearImpulse = new Button { Content = "Clear FIR" };
        clearImpulse.Click += (_, _) => SetImpulse(null);
        filters.Children.Add(convolution);
        filters.Children.Add(clearImpulse);
        shaping.Children.Add(filters);
        DspPanel.Children.Add(new Expander { Header = "Loudness, dither and convolution", Content = shaping, HorizontalAlignment = HorizontalAlignment.Stretch });
        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        presetRow.Children.Add(presetMenu);
        var save = new Button { Content = "Save" };
        save.Click += SavePreset;
        presetRow.Children.Add(save);
        var reset = new Button { Content = "Reset" };
        reset.Click += (_, _) => LoadDsp(DspSettings.Flat());
        presetRow.Children.Add(reset);
        DspPanel.Children.Add(presetRow);
        presetMenu.ItemsSource = library.Presets().Keys.ToArray();
        presetMenu.SelectionChanged += (_, _) =>
        {
            if (presetMenu.SelectedItem is string name && library.Presets().TryGetValue(name, out var saved))
                LoadDsp(saved);
        };
        DspEnabled.Toggled += (_, _) => UpdateDsp();
        dspControlsBuilt = true;
        LoadDsp(dsp);
    }

    private CheckBox AddCheck(Panel panel, string label)
    {
        var check = new CheckBox { Content = label };
        check.Click += (_, _) => UpdateDsp();
        panel.Children.Add(check);
        return check;
    }

    private ComboBox AddChoice(Panel panel, string label, IEnumerable<string> items, int value = 0)
    {
        var menu = new ComboBox { Header = label, ItemsSource = items.ToArray(), SelectedIndex = value };
        menu.SelectionChanged += (_, _) => UpdateDsp();
        panel.Children.Add(menu);
        return menu;
    }

    private void AddBand(EqBand band)
    {
        var container = new StackPanel { Spacing = 4 };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var active = new CheckBox { IsChecked = band.Active, MinWidth = 0 };
        active.Click += (_, _) => UpdateDsp();
        controls.Children.Add(active);
        var shape = AddChoice(controls, "", Enum.GetNames<FilterShape>(), (int)band.Shape);
        var side = AddChoice(controls, "", Enum.GetNames<EqChannel>(), (int)band.Channel);
        var remove = new Button { Content = "×" };
        controls.Children.Add(remove);
        var values = new Grid { ColumnSpacing = 5 };
        var row = new BandControls(AddNumber(values, "Hz", 20, 22000, band.Frequency),
            AddNumber(values, "dB", -30, 24, band.Gain), AddNumber(values, "Q", .05, 30, band.Q),
            shape, side, active);
        remove.Click += (_, _) => { bandPanel.Children.Remove(container); eqBands.Remove(row); UpdateDsp(); };
        container.Children.Add(controls);
        container.Children.Add(values);
        bandPanel.Children.Add(container);
        eqBands.Add(row);
    }

    private async void ImportAutoEq(object sender, RoutedEventArgs args)
    {
        try
        {
            var file = await PickFile(".txt");
            if (file is null)
                return;
            var preset = AutoEq.Read(await File.ReadAllTextAsync(file.Path));
            LoadDsp(preset);
            Status.Text = $"Imported {preset.Parametric.Length} EQ bands from {file.Name}.";
        }
        catch (Exception error) { ShowError("Importing the EQ preset", error); }
    }

    private async void LoadImpulse(object sender, RoutedEventArgs args) =>
        await RunUiAction("Loading the FIR impulse response", LoadImpulseAsync);

    private async Task LoadImpulseAsync()
    {
        var file = await PickFile(".wav");
        if (file is not null)
            SetImpulse(file.Path);
    }

    private void SetImpulse(string? path)
    {
        dsp = dsp with { ImpulsePath = path };
        convolution.Content = path is null ? "Load FIR WAV" : System.IO.Path.GetFileName(path);
        if (track is not null)
            Start(playback?.Position ?? Seek.Value, playback?.State == NAudio.Wave.PlaybackState.Playing);
    }

    private Slider AddSlider(StackPanel panel, string label, double min, double max, double value)
    {
        var slider = new Slider { Header = $"{label} · {value:0.#}", Minimum = min, Maximum = max,
            Value = value, StepFrequency = .5, SmallChange = .5 };
        slider.ValueChanged += (_, args) => { slider.Header = $"{label} · {args.NewValue:0.#}"; UpdateDsp(); };
        panel.Children.Add(slider);
        return slider;
    }

    private NumberBox AddNumber(Grid row, string label, double min, double max, double value)
    {
        var number = new NumberBox { Header = label, Minimum = min, Maximum = max, Value = value, MinWidth = 70 };
        Grid.SetColumn(number, row.ColumnDefinitions.Count);
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(number);
        number.ValueChanged += (_, _) => UpdateDsp();
        return number;
    }

    private void EqResized(object sender, SizeChangedEventArgs args) => DrawEq();

    private void DrawEq()
    {
        if (!dspControlsBuilt)
            return;
        int rate = playback?.WaveFormat.SampleRate ?? 48000;
        void Draw(Polyline line, EqChannel channel)
        {
            var filters = dsp.Filters(rate, channel);
            line.Points = Points(120, EqChart, i =>
            {
                double frequency = 20 * Math.Pow(1000, i / 119.0);
                double gain = dsp.Enabled ? dsp.Preamp + filters.Sum(filter => filter.Response(frequency, rate)) : 0;
                return .5 - Math.Clamp(gain, -36, 36) / 76;
            });
        }
        Draw(rightEqLine, EqChannel.Right);
        Draw(eqLine, EqChannel.Left);
    }

    private static double Value(NumberBox control, double fallback) =>
        double.IsNaN(control.Value) ? fallback : control.Value;

    private void UpdateDsp()
    {
        if (!ready || updatingDsp)
            return;
        dsp = dsp with
        {
            Enabled = DspEnabled.IsOn, Preamp = preamp.Value, Bass = bass.Value, Width = width.Value,
            Crossfeed = crossfeed.Value, Balance = balance.Value / 100, Invert = invert.IsChecked == true,
            Limiter = limiter.IsChecked == true, Headroom = headroom.Value,
            Dither = (DitherMode)Math.Max(0, dither.SelectedIndex), Normalization = (LoudnessMode)Math.Max(0, loudness.SelectedIndex),
            Parametric = eqBands.Select(band => new EqBand(Value(band.Frequency, 1000), Value(band.Gain, 0),
                Value(band.Q, 1), (FilterShape)band.Shape.SelectedIndex, (EqChannel)band.Channel.SelectedIndex,
                band.Active.IsChecked == true)).ToArray(),
            Graphic = graphics.Select(band => band.Value).ToArray()
        };
        playback?.Update(dsp, DigitalVolume());
        if (playback is not null)
            ShowSignalPath(playback);
        DrawEq();
    }

    private void LoadDsp(DspSettings preset)
    {
        string? previousImpulse = dsp.ImpulsePath;
        dsp = preset;
        if (!dspControlsBuilt)
            return;
        updatingDsp = true;
        DspEnabled.IsOn = preset.Enabled;
        preamp.Value = preset.Preamp;
        bass.Value = preset.Bass;
        width.Value = preset.Width;
        crossfeed.Value = preset.Crossfeed;
        balance.Value = preset.Balance * 100;
        invert.IsChecked = preset.Invert;
        limiter.IsChecked = preset.Limiter;
        headroom.Value = preset.Headroom;
        dither.SelectedIndex = (int)preset.Dither;
        loudness.SelectedIndex = (int)preset.Normalization;
        convolution.Content = preset.ImpulsePath is string path ? System.IO.Path.GetFileName(path) : "Load FIR WAV";
        eqBands.Clear();
        bandPanel.Children.Clear();
        foreach (var band in preset.Parametric)
            AddBand(band);
        for (int i = 0; i < graphics.Length; i++)
            graphics[i].Value = preset.Graphic[i];
        updatingDsp = false;
        UpdateDsp();
        if (ready && !changingDevice && previousImpulse != preset.ImpulsePath && track is not null)
            Start(playback?.Position ?? Seek.Value, playback?.State == NAudio.Wave.PlaybackState.Playing);
    }

    private async void SavePreset(object sender, RoutedEventArgs args) =>
        await RunUiAction("Saving the DSP preset", SavePresetAsync);

    private async Task SavePresetAsync()
    {
        var name = new TextBox { PlaceholderText = "Preset name" };
        var dialog = new ContentDialog
        {
            Title = "Save DSP preset", Content = name, PrimaryButtonText = "Save",
            CloseButtonText = "Cancel", XamlRoot = Root.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && name.Text.Trim() is { Length: > 0 } title)
        {
            library.SavePreset(title, dsp);
            presetMenu.ItemsSource = library.Presets().Keys.ToArray();
            presetMenu.SelectedItem = title;
            Status.Text = $"Saved {title}.";
        }
    }
}
