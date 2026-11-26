using System.Numerics;

namespace Soundroom;

internal sealed record ImpulseResponse(double[] Left, double[] Right)
{
    public static ImpulseResponse Load(string path, int rate)
    {
        var track = Media.ProbeAsync(path).GetAwaiter().GetResult();
        if (track.Channels is < 1 or > 2 || !track.Codec.StartsWith("pcm_", StringComparison.Ordinal))
            throw new IOException("Select a mono or stereo PCM WAV impulse response.");
        using var input = new PcmDecoder(track, 0, rate, "f64le", new PlaybackOptions().ResamplingFilter(rate));
        const int max = 131072;
        var bytes = new byte[(max + 1) * track.Channels * sizeof(double)];
        int length = 0, read;
        while (length < bytes.Length && (read = input.Read(bytes.AsSpan(length))) > 0)
            length += read;
        int frames = length / (track.Channels * sizeof(double));
        if (frames == 0 || frames > max)
            throw new IOException($"Impulse responses must contain 1–{max:N0} samples at {rate:N0} Hz.");
        var data = new double[frames * track.Channels];
        Buffer.BlockCopy(bytes, 0, data, 0, frames * track.Channels * sizeof(double));
        if (data.Any(sample => !double.IsFinite(sample)))
            throw new IOException("The impulse response contains non-finite samples.");
        if (track.Channels == 1)
            return new(data, data);
        return new(data.Where((_, i) => i % 2 == 0).ToArray(), data.Where((_, i) => i % 2 == 1).ToArray());
    }
}

internal sealed class StereoConvolution(ImpulseResponse impulse)
{
    private readonly PartitionedFir left = new(impulse.Left), right = new(impulse.Right);
    public void Apply(double[] samples, int frames)
    {
        left.Apply(samples, frames, 0);
        right.Apply(samples, frames, 1);
    }

    private sealed class PartitionedFir
    {
        private const int Block = Binaural.Block, Size = Block * 2;
        private readonly Complex[][] kernel, history;
        private readonly Complex[] sum = new Complex[Size];
        private readonly double[] overlap = new double[Block];
        private int position;

        public PartitionedFir(double[] impulse)
        {
            int partitions = (impulse.Length + Block - 1) / Block;
            kernel = new Complex[partitions][];
            history = new Complex[partitions][];
            for (int partition = 0; partition < partitions; partition++)
            {
                var spectrum = kernel[partition] = new Complex[Size];
                history[partition] = new Complex[Size];
                for (int i = 0; i < Math.Min(Block, impulse.Length - partition * Block); i++)
                    spectrum[i] = impulse[partition * Block + i];
                Fft.Transform(spectrum);
            }
        }

        public void Apply(double[] audio, int frames, int channel)
        {
            var current = history[position];
            for (int i = 0; i < Block; i++)
            {
                current[i] = overlap[i];
                current[i + Block] = overlap[i] = i < frames ? audio[i * 2 + channel] : 0;
            }
            Fft.Transform(current);
            Array.Clear(sum);
            for (int i = 0; i < kernel.Length; i++)
            {
                var input = history[(position - i + history.Length) % history.Length];
                var filter = kernel[i];
                for (int bin = 0; bin < Size; bin++)
                    sum[bin] += input[bin] * filter[bin];
            }
            Fft.Transform(sum, inverse: true);
            for (int i = 0; i < frames; i++)
                audio[i * 2 + channel] = sum[i + Block].Real;
            position = (position + 1) % history.Length;
        }
    }
}

internal static class Fft
{
    public static void Transform(Complex[] values, bool inverse = false)
    {
        int n = values.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            while ((j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }
            j ^= bit;
            if (i < j)
                (values[i], values[j]) = (values[j], values[i]);
        }
        for (int span = 2; span <= n; span *= 2)
        {
            Complex step = Complex.FromPolarCoordinates(1, (inverse ? 2 : -2) * Math.PI / span);
            for (int start = 0; start < n; start += span)
            {
                Complex twiddle = Complex.One;
                for (int i = 0; i < span / 2; i++)
                {
                    Complex lo = values[start + i], hi = values[start + i + span / 2] * twiddle;
                    values[start + i] = lo + hi;
                    values[start + i + span / 2] = lo - hi;
                    twiddle *= step;
                }
            }
        }
        if (inverse)
            for (int i = 0; i < n; i++)
                values[i] /= n;
    }
}
