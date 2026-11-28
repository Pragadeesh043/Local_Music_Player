using NAudio.Dsp;
using NAudio.Wave;

namespace Soundroom;

public sealed partial class MainWindow
{
    private bool liveSignal;
    private Signal? lastSignal;
    private readonly Complex[] fft = new Complex[512];

    private WebSignalPath? ReadWebSignalPath()
    {
        if (playback is not { } session || session.CurrentTrack.Key != track?.Key)
            return null;
        string mode = session.OutputRoute switch
        {
            Route.Binaural => "Shared binaural",
            Route.Downmix => "Shared stereo",
            Route.Cable => "Shared 7.1",
            Route.Exclusive => "Exclusive PCM",
            Route.ExclusiveDsp => "Exclusive PCM (DSP route)",
            Route.Dop => "Exclusive DoP",
            Route.NativeDsd => "ASIO DSD",
            _ => "Unknown route"
        };
        int? pcmRate = session.OutputRoute is Route.Dop or Route.NativeDsd ? null : session.WaveFormat.SampleRate;
        return new(mode, session.IsProcessed && session.ReadProcessing(out _, out _).Controls.Dsp.Enabled, pcmRate);
    }

    private object? ListenSignal(bool enabled)
    {
        liveSignal = enabled;
        lastSignal = null;
        playback?.SetAnalysisEnabled(enabled && presentationVisible);
        return enabled && playback is { } current && current.Latest is { } signal ? SignalData(current, signal) : null;
    }

    private void PublishLiveSignal(Playback session)
    {
        if (!liveSignal || !presentationVisible || session.State != PlaybackState.Playing ||
            session.Latest is not { } signal || ReferenceEquals(signal, lastSignal))
            return;
        lastSignal = signal;
        PostGlassroom(new { v = 1, @event = "signal", data = SignalData(session, signal) });
    }

    private object SignalData(Playback session, Signal signal)
    {
        double[] spectrum = [];
        if (signal.Waveform.Length > 0)
        {
            for (int i = 0; i < fft.Length; i++)
            {
                fft[i].X = i < signal.Waveform.Length ? signal.Waveform[i] * (float)FastFourierTransform.HannWindow(i, fft.Length) : 0;
                fft[i].Y = 0;
            }
            FastFourierTransform.FFT(true, 9, fft);
            spectrum = Enumerable.Range(0, 96).Select(index =>
            {
                int bin = (int)Math.Pow(255, index / 95.0);
                return 20 * Math.Log10(Math.Max(1e-9, Math.Sqrt(fft[bin].X * fft[bin].X + fft[bin].Y * fft[bin].Y)));
            }).ToArray();
        }
        return new { trackId = session.CurrentTrack.Key, source = signal.Source, output = signal.Output, signal.Waveform, signal.WaveChannel,
            signal.TruePeak, signal.Lufs, signal.Correlation, signal.Phase, signal.LimiterReductionDb, spectrum,
            sourceChannels = session.SourceChannels, outputChannels = session.OutputChannels };
    }

    private async Task<object?> ReadSignalReport()
    {
        if (playback is not { } session || session.CurrentTrack.Key != track?.Key)
            return null;
        var current = session.CurrentTrack;
        var snapshot = session.ReadProcessing(out bool applied, out bool pending);
        var format = session.WaveFormat;
        var route = new SignalRoute(session.OutputRoute, session.Device, format.SampleRate, session.OutputBits,
            format.BitsPerSample, session.SourceChannels, session.OutputChannels, session.AsioDriver,
            session.MinimumPhase, session.ImpulsePath);
        var report = SignalPathReport.Describe(current, route, snapshot, HardwareVolume);
        string notice = !applied ? "Configured route. Waiting for the first audio block."
            : pending && session.IsProcessed ? "Showing delivered blocks. New DSP settings are still queued."
            : session.State == PlaybackState.Paused ? "Paused. Showing the last delivered audio block." : "";
        bool asio = session.OutputRoute == Route.NativeDsd;
        double? rate = asio ? session.ReadAsioSampleRate() : null;
        HardwareOutput? hardware = null;
        if (!asio)
        {
            try { hardware = await Task.Run(() => HardwareOutput.Read(session.Device.Id)); }
            catch (Exception error) { Diagnostics.Write("Reading hardware controls", error); }
        }
        return ReferenceEquals(session, playback) && current.Key == track?.Key ? new { report, notice, asio, rate, hardware } : null;
    }

    private void SetHardwareMute(bool muted)
    {
        if (playback is not { } session || session.OutputRoute == Route.NativeDsd)
            throw new InvalidOperationException("Hardware mute is unavailable for this route.");
        HardwareOutput.SetMute(session.Device.Id, muted);
    }
}
