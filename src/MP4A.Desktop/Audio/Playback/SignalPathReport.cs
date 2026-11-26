namespace Soundroom;

internal sealed record SignalStage(string Name, string Summary, string Detail);

internal sealed record SignalRoute(Route Mode, OutputDevice Device, int Rate, int Bits, int ContainerBits,
    string[] InputChannels, string[] OutputChannels, string? AsioDriver = null,
    bool MinimumPhase = false, string? ImpulsePath = null);

internal sealed record SignalPathReport(string Verdict, bool ExactAtOutput, string Explanation,
    string OutputSummary, IReadOnlyList<SignalStage> Stages)
{
    public static SignalPathReport Describe(Track track, SignalRoute route, ProcessingSnapshot processing,
        bool hardwareVolume)
    {
        var stages = new List<SignalStage>();
        void Add(string name, string summary, string detail) => stages.Add(new(name, summary, detail));
        var mode = route.Mode;
        bool native = mode == Route.NativeDsd, dop = mode == Route.Dop;
        bool exclusive = mode is Route.Exclusive or Route.ExclusiveDsp or Route.Dop;
        bool directPcm = mode == Route.Exclusive;
        bool payload = native || dop;
        bool processed = mode is Route.Binaural or Route.Downmix or Route.ExclusiveDsp;
        var settings = processing.Controls.Dsp;
        string sourceBits = track.Bits > 0 ? $"{track.Bits}-bit source" : "Source bit depth unspecified";
        Add("Source", track.Summary, $"{track.Title} · {sourceBits}\nChannels: {string.Join(", ", route.InputChannels)}");

        if (payload)
        {
            Add("DSD payload", "Original one-bit samples", "Soundroom reads the DSD payload directly and bypasses PCM DSP and software gain.");
            if (dop)
                Add("DoP packing", $"{Rate(route.Rate)} PCM carrier", "Each carrier frame contains 16 DSD bits per channel and an alternating DoP marker.");
        }
        else
        {
            Add("Decode", directPcm ? "Integer PCM" : "64-bit floating-point PCM",
                directPcm ? "Decoded integer samples bypass the float DSP pipeline."
                : "The decoder supplies PCM frames for processing. DSD sources on this route are decoded to PCM.");
            if (track.Rate != route.Rate)
                Add("Resample", $"{Rate(track.Rate)} → {Rate(route.Rate)}",
                    route.MinimumPhase ? "Minimum-phase resampling in Soundroom." : "Linear-phase resampling in the decoder.");
        }

        if (mode == Route.Binaural)
            Add("Binaural rendering", $"{route.InputChannels.Length} → 2 channels" +
                (Speakers.HasHeight(route.InputChannels) ? " · Includes height channels" : ""),
                "Soundroom convolves the audio in 64-bit floats using impulse responses prepared from Steam Audio’s HRTF.");
        else if (processed && route.InputChannels.Length != route.OutputChannels.Length)
            Add("Channel mix", $"{route.InputChannels.Length} → {route.OutputChannels.Length} channels", "Soundroom mixes the source layout to stereo.");
        else if (mode == Route.Cable)
            Add("Channel mapping", $"{route.InputChannels.Length} → 8 channels",
                "Soundroom maps known speaker positions to the 7.1 endpoint. HeSuVi and other external processing are outside this view.");

        if (processed && settings.Enabled)
        {
            var filters = new List<string>();
            int peq = settings.Parametric.Count(band => band.Active && (band.Gain != 0 || band.Shape != FilterShape.Peak));
            int geq = settings.Graphic.Where((gain, index) => gain != 0 && DspSettings.Centers[index] < route.Rate * .49).Count();
            if (peq > 0) filters.Add($"{peq} parametric bands");
            if (geq > 0) filters.Add($"{geq} graphic bands");
            if (settings.Bass != 0) filters.Add($"Bass shelf {Db(settings.Bass)}");
            if (settings.Width != 100) filters.Add($"Stereo width {settings.Width:0}%");
            if (settings.Crossfeed != 0) filters.Add($"Crossfeed {settings.Crossfeed:0}%");
            if (settings.Balance != 0) filters.Add($"Balance {settings.Balance * 100:+0;-0}%");
            if (settings.Invert) filters.Add("Polarity inversion");
            if (settings.Preamp != 0) filters.Add($"Preamp {Db(settings.Preamp)}");
            if (filters.Count > 0)
                Add("Headphone DSP", $"{filters.Count} active settings", string.Join("\n", filters));
            if (settings.Normalization != LoudnessMode.Off)
                Add("Loudness normalization", $"{LoudnessName(settings.Normalization)} · {Db(processing.NormalizationDb)}",
                    $"Gain target for the delivered block. Configured headroom is {settings.Headroom:0.#} dB.");
        }
        else
            Add("Headphone DSP", "Bypassed", processed ? "The headphone DSP switch is off." : "This output route bypasses the headphone DSP chain.");

        string gain = processed ? $"{processing.Controls.DigitalVolume:P0} digital gain" : "Unity digital gain";
        Add("Volume", hardwareVolume ? "Endpoint hardware volume" : gain,
            hardwareVolume ? $"Soundroom requests hardware volume from the Windows endpoint. {gain} in the app."
                : processed ? "Digital gain is applied together with the normalization gain and smoothed during changes."
                : "Soundroom leaves sample values untouched at the volume stage.");
        if (processed && settings.Enabled && route.ImpulsePath is { } impulse)
            Add("FIR convolution", System.IO.Path.GetFileName(impulse), "The loaded impulse response is applied after digital gain.");
        if (processed && settings.Enabled && settings.Limiter)
            Add("Peak limiter", $"Ceiling −{Math.Max(.1, settings.Headroom):0.#} dB", "The limiter uses an oversampled peak estimate.");

        string output = native ? $"1-bit DSD · {Rate(track.SourceRate)}" :
            $"{route.Bits}-bit {(dop ? "DoP carrier" : exclusive ? "PCM" : "float PCM")} · {Rate(route.Rate)}";
        string container = route.Bits < route.ContainerBits ? $" in a {route.ContainerBits}-bit container" : "";
        if (!native && !dop)
        {
            string quantization = mode == Route.ExclusiveDsp
                ? $"Quantized to {route.Bits} valid bits{container}. Dither: {DitherName(settings.Dither)}."
                : directPcm ? $"{track.Bits} source bits fit in {route.Bits} valid bits{container}." +
                    (track.Bits < route.ContainerBits ? " Extra bits are padding." : "")
                : "Soundroom packs the submitted stream as 32-bit float PCM.";
            Add("Output encoding", output, quantization);
        }

        string driver = native ? "ASIO native DSD" : exclusive ? "WASAPI exclusive" : "WASAPI shared";
        Add("Output driver", driver, native
            ? $"The selected ASIO driver accepted the one-bit mode and reported the requested DSD clock. {route.AsioDriver}"
            : exclusive
                ? $"Windows accepted {output}{container} with {route.OutputChannels.Length} channels in exclusive mode."
                : $"Submitted {output} / {route.OutputChannels.Length} ch. Windows mix: {Rate(route.Device.Rate)} / {route.Device.MixBits}-bit / {route.Device.Channels} ch.");
        Add("Output endpoint", native ? route.AsioDriver ?? "ASIO driver" : route.Device.Name,
            "The route is verified through the driver interface. Soundroom has no readback of the DAC’s internal clock, processing, or DoP lock.");

        bool unchanged = directPcm || payload;
        return new(directPcm ? "Bit-perfect PCM to the driver" : payload ? "DSD payload preserved to the driver" :
            mode == Route.Cable ? "Shared output to external processing" : "Processed playback", unchanged,
            directPcm ? "Decoded PCM samples are sent at their source rate and precision. Pause and resume apply short boundary fades to prevent clicks."
            : payload ? "The one-bit stream bypasses DSP. This describes the payload delivered by Soundroom."
            : exclusive ? "Exclusive access bypasses the Windows mixer. This route converts samples through the DSP and output quantizer."
            : "This route uses the Windows shared mixer. Sample preservation through the endpoint is unverified.",
            $"{driver} · {output}", stages);
    }

    private static string Rate(int value) => $"{value / 1000.0:0.###} kHz";
    private static string Db(double value) => $"{value:+0.0;-0.0;0.0} dB";
    private static string LoudnessName(LoudnessMode mode) => mode switch
    {
        LoudnessMode.ReplayGainTrack => "ReplayGain track",
        LoudnessMode.ReplayGainAlbum => "ReplayGain album",
        LoudnessMode.R128Track => "R128 track",
        LoudnessMode.R128Album => "R128 album",
        _ => "Off"
    };
    private static string DitherName(DitherMode mode) => mode switch
    {
        DitherMode.Tpdf => "TPDF",
        DitherMode.NoiseShaped => "noise-shaped",
        _ => "off"
    };
}
