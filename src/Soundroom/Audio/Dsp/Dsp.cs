using System.Buffers.Binary;
using System.Numerics;

namespace Soundroom;

internal enum FilterShape { Peak, LowShelf, HighShelf, LowPass, HighPass, Notch, BandPass, AllPass }
internal enum EqChannel { Both, Left, Right }
internal enum DitherMode { Off, Tpdf, NoiseShaped }
internal enum LoudnessMode { Off, ReplayGainTrack, ReplayGainAlbum, R128Track, R128Album }

internal sealed record EqBand(double Frequency, double Gain, double Q = 1,
    FilterShape Shape = FilterShape.Peak, EqChannel Channel = EqChannel.Both, bool Active = true);

internal sealed record DspSettings(bool Enabled, double Preamp, double Bass, double Width,
    double Crossfeed, EqBand[] Parametric, double[] Graphic)
{
    public double Balance { get; init; }
    public bool Invert { get; init; }
    public DitherMode Dither { get; init; }
    public LoudnessMode Normalization { get; init; }
    public double Headroom { get; init; } = 1;
    public bool Limiter { get; init; }
    public string? ImpulsePath { get; init; }
    public static readonly int[] Centers = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    public static DspSettings Flat() => new(true, 0, 0, 100, 0,
        [new(100, 0), new(1000, 0), new(5000, 0)], new double[Centers.Length]);

    public Filter[] Filters(int rate, EqChannel channel = EqChannel.Left)
    {
        if (!Enabled)
            return [];
        var filters = new List<Filter>();
        if (Bass != 0)
            filters.Add(Filter.Create(rate, 85, Bass, .7071, FilterShape.LowShelf));
        foreach (var band in Parametric.Where(b => b.Active && (b.Channel == EqChannel.Both || b.Channel == channel)))
            filters.Add(Filter.Create(rate, band.Frequency, band.Gain, band.Q, band.Shape));
        for (int i = 0; i < Centers.Length; i++)
            if (Graphic[i] != 0 && Centers[i] < rate * .49)
                filters.Add(Filter.Create(rate, Centers[i], Graphic[i], 1.4));
        return filters.ToArray();
    }

    public double NormalizationGain(Track track)
    {
        double target = Normalization switch
        {
            LoudnessMode.ReplayGainTrack => LoudnessTag(track, "replaygain_track_gain") ??
                track.Analysis?.ReplayGain ?? 0,
            LoudnessMode.ReplayGainAlbum => LoudnessTag(track, "replaygain_album_gain") ??
                track.Analysis?.AlbumReplayGain ?? 0,
            LoudnessMode.R128Track => track.Analysis?.Lufs is double r128t ? -23 - r128t :
                (LoudnessTag(track, "r128_track_gain") ?? 0) / 256,
            LoudnessMode.R128Album => track.Analysis?.AlbumLufs is double r128a ? -23 - r128a :
                (LoudnessTag(track, "r128_album_gain") ?? 0) / 256,
            _ => 0
        };
        if (Normalization != LoudnessMode.Off && track.Analysis?.TruePeak is double peak)
            target = Math.Min(target, -Headroom - peak);
        return Math.Clamp(target, -60, 24);
    }

    private static double? LoudnessTag(Track track, string tag)
    {
        string? value = track.Tags.FirstOrDefault(pair => pair.Key.Equals(tag, StringComparison.OrdinalIgnoreCase)).Value;
        return double.TryParse(value?.Replace("dB", "", StringComparison.OrdinalIgnoreCase),
            System.Globalization.CultureInfo.InvariantCulture, out double gain) ? gain : null;
    }
}

internal struct Filter(double b0, double b1, double b2, double a1, double a2)
{
    private double z1, z2;
    public double Apply(double sample)
    {
        double output = sample * b0 + z1;
        z1 = sample * b1 + z2 - a1 * output;
        z2 = sample * b2 - a2 * output;
        return output;
    }

    public readonly double Response(double frequency, int rate)
    {
        var z = Complex.FromPolarCoordinates(1, -2 * Math.PI * frequency / rate);
        return 20 * Math.Log10(Math.Max(1e-20, Complex.Abs((b0 + b1 * z + b2 * z * z) /
            (1 + a1 * z + a2 * z * z))));
    }

    public static Filter Create(int rate, double frequency, double gain, double q, FilterShape shape = FilterShape.Peak)
    {
        double omega = 2 * Math.PI * Math.Clamp(frequency, 10, rate * .499) / rate;
        double a = Math.Pow(10, gain / 40);
        double c = Math.Cos(omega), s = Math.Sin(omega), alpha = s / (2 * Math.Max(.05, q));
        double p = a + 1, m = a - 1, beta = 2 * Math.Sqrt(a) * alpha;
        (double b0, double b1, double b2, double a0, double a1, double a2) = shape switch
        {
            FilterShape.LowShelf => (a * (p - m * c + beta), 2 * a * (m - p * c), a * (p - m * c - beta),
                p + m * c + beta, -2 * (m + p * c), p + m * c - beta),
            FilterShape.HighShelf => (a * (p + m * c + beta), -2 * a * (m + p * c), a * (p + m * c - beta),
                p - m * c + beta, 2 * (m - p * c), p - m * c - beta),
            FilterShape.LowPass => ((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + alpha, -2 * c, 1 - alpha),
            FilterShape.HighPass => ((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + alpha, -2 * c, 1 - alpha),
            FilterShape.Notch => (1, -2 * c, 1, 1 + alpha, -2 * c, 1 - alpha),
            FilterShape.BandPass => (alpha, 0, -alpha, 1 + alpha, -2 * c, 1 - alpha),
            FilterShape.AllPass => (1 - alpha, -2 * c, 1 + alpha, 1 + alpha, -2 * c, 1 - alpha),
            _ => (1 + alpha * a, -2 * c, 1 - alpha * a, 1 + alpha / a, -2 * c, 1 - alpha / a)
        };
        return new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }
}

internal sealed class StereoDsp(int rate, ImpulseResponse? impulse = null)
{
    private DspSettings? active, previous;
    private StereoFilters? filters, oldFilters;
    private int fadeRemaining;
    private double smoothedVolume = -1;
    private readonly int fadeFrames = Math.Max(1, rate / 100);
    private readonly StereoConvolution? fir = impulse is null ? null : new(impulse);
    private readonly TruePeakProcessor limiter = new(rate);

    public void Apply(double[] samples, int frames, DspSettings settings, double volume, Track track)
    {
        if (!ReferenceEquals(active, settings))
        {
            previous = active;
            oldFilters = filters;
            filters = new StereoFilters(rate, settings);
            active = settings;
            fadeRemaining = previous is null ? 0 : fadeFrames;
        }
        double targetVolume = volume * Math.Pow(10, settings.Enabled ? settings.NormalizationGain(track) / 20 : 0);
        if (smoothedVolume < 0)
            smoothedVolume = targetVolume;
        for (int i = 0; i < frames * 2; i += 2)
        {
            (double l, double r) = filters!.Apply(samples[i], samples[i + 1]);
            if (fadeRemaining > 0 && oldFilters is not null)
            {
                var old = oldFilters.Apply(samples[i], samples[i + 1]);
                double mix = (double)fadeRemaining-- / fadeFrames;
                l += (old.Left - l) * mix;
                r += (old.Right - r) * mix;
            }
            smoothedVolume += (targetVolume - smoothedVolume) * (1.0 / fadeFrames);
            samples[i] = l * smoothedVolume;
            samples[i + 1] = r * smoothedVolume;
        }
        if (settings.Enabled)
        {
            fir?.Apply(samples, frames);
            if (settings.Limiter)
                limiter.Limit(samples, frames, settings.Headroom);
        }
    }

    private sealed class StereoFilters(int rate, DspSettings settings)
    {
        private readonly Filter[] left = settings.Filters(rate), right = settings.Filters(rate, EqChannel.Right);
        private readonly double[] delayLeft = new double[Math.Max(1, rate * 3 / 10000)], delayRight = new double[Math.Max(1, rate * 3 / 10000)];
        private readonly double lowPass = 1 - Math.Exp(-2 * Math.PI * 700 / rate);
        private readonly double gain = Math.Pow(10, settings.Preamp / 20) * (settings.Invert ? -1 : 1);
        private int delayPosition;
        private double lowLeft, lowRight;

        public (double Left, double Right) Apply(double l, double r)
        {
            if (!settings.Enabled)
                return (l, r);
            for (int i = 0; i < left.Length; i++)
                l = left[i].Apply(l);
            for (int i = 0; i < right.Length; i++)
                r = right[i].Apply(r);
            double mid = (l + r) * .5, side = (l - r) * settings.Width / 200;
            l = mid + side;
            r = mid - side;
            lowLeft += lowPass * (l - lowLeft);
            lowRight += lowPass * (r - lowRight);
            double amount = settings.Crossfeed / 200;
            double lOut = l * (1 - amount) + delayRight[delayPosition] * amount;
            double rOut = r * (1 - amount) + delayLeft[delayPosition] * amount;
            delayRight[delayPosition] = lowRight;
            delayLeft[delayPosition] = lowLeft;
            delayPosition = (delayPosition + 1) % delayLeft.Length;
            return (lOut * gain * (1 - Math.Max(0, settings.Balance)),
                rOut * gain * (1 + Math.Min(0, settings.Balance)));
        }
    }
}

internal sealed class Quantizer
{
    private readonly Random random = new();
    private readonly double[] previousError = new double[2];
    public void Write(Span<byte> output, double sample, int valid, DitherMode mode, int channel)
    {
        double scale = Math.Pow(2, valid - 1);
        double shaped = sample * scale;
        if (mode == DitherMode.NoiseShaped)
            shaped += .85 * previousError[channel];
        double noise = mode == DitherMode.Off ? 0 : random.NextDouble() - random.NextDouble();
        int value = (int)Math.Clamp(Math.Round(shaped + noise), -scale, scale - 1);
        previousError[channel] = Math.Clamp(shaped - value, -1, 1);
        value <<= output.Length * 8 - valid;
        for (int i = 0; i < output.Length; i++)
            output[i] = (byte)(value >> (i * 8));
    }

    public static double Decode(ReadOnlySpan<byte> bytes) => bytes.Length switch
    {
        2 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768.0,
        3 => ((bytes[2] << 24) | (bytes[1] << 16) | (bytes[0] << 8)) / 2147483648.0,
        4 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0,
        _ => throw new ArgumentOutOfRangeException(nameof(bytes))
    };
}
