using System.Numerics;

namespace Soundroom;

internal readonly record struct PhasePoint(float Side, float Mid);

internal sealed class TruePeakProcessor(int rate, int channels = 2)
{
    private const int Taps = 64;
    private const double LimiterMarginDb = .6;
    private static readonly double[][] Kernel = BuildKernel();
    private readonly double[][] history = Enumerable.Range(0, channels).Select(_ => new double[Taps * 2]).ToArray();
    private double[]? limiterWindow;
    private double[][]? limiterHistory;
    private int position;
    private double gain = 1;
    public double GainReductionDb => -20 * Math.Log10(Math.Max(gain, 1e-9));

    private static double[][] BuildKernel() => Enumerable.Range(1, 3).Select(phase =>
    {
        var row = new double[Taps];
        for (int tap = 0; tap < Taps; tap++)
        {
            double x = tap - (Taps / 2 - 1) - phase / 4.0;
            double window = .42 - .5 * Math.Cos(2 * Math.PI * tap / (Taps - 1)) +
                .08 * Math.Cos(4 * Math.PI * tap / (Taps - 1));
            row[tap] = Math.Sin(Math.PI * x) / (Math.PI * x) * window;
        }
        double sum = row.Sum();
        return row.Select(value => value / sum).ToArray();
    }).ToArray();

    public double Measure(double[] stereo, int frames)
    {
        double peak = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                double sample = stereo[frame * channels + channel];
                peak = Math.Max(peak, Math.Abs(sample));
                var buffer = history[channel];
                buffer[position] = buffer[position + Taps] = sample;
                foreach (double[] coefficient in Kernel)
                {
                    double value = Convolve(coefficient, buffer, position + 1);
                    peak = Math.Max(peak, Math.Abs(value));
                }
            }
            position = (position + 1) % Taps;
        }
        return peak;
    }

    private static double Convolve(double[] coefficients, double[] samples, int start)
    {
        if (Vector.IsHardwareAccelerated)
        {
            var sum = Vector<double>.Zero;
            for (int i = 0; i < Taps; i += Vector<double>.Count)
                sum += new Vector<double>(coefficients, i) * new Vector<double>(samples, start + i);
            return Vector.Sum(sum);
        }

        double value = 0;
        for (int i = 0; i < Taps; i++)
            value += coefficients[i] * samples[start + i];
        return value;
    }

    public void Limit(double[] samples, int frames, double headroom)
    {
        int overlap = Taps - 1;
        int end = overlap + frames;
        if (limiterWindow is null || limiterWindow.Length < end + overlap)
            limiterWindow = new double[end + overlap];
        limiterHistory ??= Enumerable.Range(0, channels).Select(_ => new double[overlap]).ToArray();
        var window = limiterWindow;
        // Allow for peaks found by a different reconstruction filter or between our sample phases.
        double ceiling = Math.Pow(10, -(Math.Max(.1, headroom) + LimiterMarginDb) / 20);
        double target = 1;
        for (int channel = 0; channel < channels; channel++)
        {
            limiterHistory[channel].CopyTo(window, 0);
            for (int frame = 0; frame < frames; frame++)
            {
                double sample = samples[frame * channels + channel];
                window[overlap + frame] = sample;
                target = Math.Min(target, ceiling / Math.Max(Math.Abs(sample), 1e-12));
            }
            Array.Clear(window, end, overlap);
            for (int start = 0; start < end; start++)
            {
                int pastTaps = Math.Max(0, overlap - start);
                foreach (double[] coefficient in Kernel)
                {
                    // At a block boundary, older samples already have their own gain.
                    double previous = 0, current = 0;
                    for (int i = 0; i < pastTaps; i++)
                        previous += coefficient[i] * window[start + i];
                    if (pastTaps == 0)
                        current = Convolve(coefficient, window, start);
                    else
                        for (int i = pastTaps; i < Taps; i++)
                            current += coefficient[i] * window[start + i];
                    if (Math.Abs(current) > 1e-12)
                        target = Math.Min(target,
                            (ceiling - Math.CopySign(1, current) * previous) / Math.Abs(current));
                }
            }
        }
        double release = Math.Exp(-frames / (rate * .12));
        gain = Math.Clamp(target, 0, 1 - (1 - gain) * release);
        for (int i = 0; i < frames * channels; i++)
            samples[i] *= gain;
        int retained = Math.Min(frames, overlap);
        for (int channel = 0; channel < channels; channel++)
        {
            var previous = limiterHistory[channel];
            Array.Copy(previous, retained, previous, 0, overlap - retained);
            for (int i = 0; i < retained; i++)
                previous[overlap - retained + i] = samples[(frames - retained + i) * channels + channel];
        }
    }

    public void ResetLimiter()
    {
        if (limiterHistory is null)
            return;
        foreach (var channel in limiterHistory)
            Array.Clear(channel);
        gain = 1;
    }
}

internal sealed class LiveAnalysis(int rate, string[] speakers)
{
    private readonly TruePeakProcessor peak = new(rate, speakers.Length);
    private readonly Filter[] shelf = speakers.Select(_ => Filter.Create(rate, 1681.974, 3.9998, .707175, FilterShape.HighShelf)).ToArray();
    private readonly Filter[] highPass = speakers.Select(_ => Filter.Create(rate, 38.1355, 0, .500327, FilterShape.HighPass)).ToArray();
    private readonly double[] weights = speakers.Select(speaker => speaker switch
    {
        "LFE" => 0, "SL" or "SR" or "BL" or "BR" => 1.41, _ => 1
    }).ToArray();
    private readonly double[] loudnessWindow = new double[Math.Max(1, (int)(rate * .4))];
    private readonly int left = Array.FindIndex(speakers, speaker => speaker is "L" or "FL");
    private readonly int right = Array.FindIndex(speakers, speaker => speaker is "R" or "FR");
    private double sum;
    private int position, count;

    public (double TruePeak, double Lufs, double? Correlation) Measure(double[] samples, int frames)
    {
        double ll = 0, rr = 0, lr = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            int start = frame * weights.Length;
            if (left >= 0 && right >= 0)
            {
                double l = samples[start + left], r = samples[start + right];
                ll += l * l;
                rr += r * r;
                lr += l * r;
            }
            double energy = 0;
            for (int channel = 0; channel < weights.Length; channel++)
            {
                double filtered = highPass[channel].Apply(shelf[channel].Apply(samples[start + channel]));
                energy += weights[channel] * filtered * filtered;
            }
            sum += energy - loudnessWindow[position];
            loudnessWindow[position] = energy;
            position = (position + 1) % loudnessWindow.Length;
        }
        count = Math.Min(loudnessWindow.Length, count + frames);
        return (peak.Measure(samples, frames),
            -.691 + 10 * Math.Log10(Math.Max(1e-16, sum / Math.Max(1, count))),
            ll * rr > 1e-20 ? Math.Clamp(lr / Math.Sqrt(ll * rr), -1, 1) : null);
    }

    public PhasePoint[]? Scope(double[] output, int frames)
    {
        if (left < 0 || right < 0)
            return null;
        var points = new PhasePoint[Math.Min(frames, 128)];
        for (int i = 0; i < points.Length; i++)
        {
            int frame = i * frames / points.Length * weights.Length;
            double l = output[frame + left], r = output[frame + right];
            points[i] = new((float)((l - r) * .5), (float)((l + r) * .5));
        }
        return points;
    }
}
