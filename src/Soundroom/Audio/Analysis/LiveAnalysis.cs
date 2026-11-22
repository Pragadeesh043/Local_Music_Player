namespace Soundroom;

internal sealed class TruePeakProcessor(int rate, int channels = 2)
{
    private const int Taps = 24;
    private static readonly double[][] Kernel = BuildKernel();
    private readonly double[][] history = Enumerable.Range(0, channels).Select(_ => new double[Taps * 2]).ToArray();
    private int position;
    private double gain = 1;
    private readonly double release = Math.Exp(-Binaural.Block / (rate * .12));

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
                    double value = 0;
                    for (int i = 0; i < Taps; i++)
                        value += coefficient[i] * buffer[position + 1 + i];
                    peak = Math.Max(peak, Math.Abs(value));
                }
            }
            position = (position + 1) % Taps;
        }
        return peak;
    }

    public void Limit(double[] samples, int frames, double headroom)
    {
        double peak = Measure(samples, frames);
        double ceiling = Math.Pow(10, -Math.Max(.1, headroom) / 20);
        double target = Math.Min(1, ceiling / Math.Max(peak, 1e-12));
        gain = Math.Min(target, 1 - (1 - gain) * release);
        for (int i = 0; i < frames * 2; i++)
            samples[i] *= gain;
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
    private double sum;
    private int position, count;

    public (double TruePeak, double Lufs, double? Correlation) Measure(double[] samples, int frames)
    {
        double ll = 0, rr = 0, lr = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            int start = frame * weights.Length;
            if (weights.Length >= 2)
            {
                double l = samples[start], r = samples[start + 1];
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
}
