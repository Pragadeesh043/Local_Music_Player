using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;

namespace Soundroom;

internal sealed class MinimumPhaseSource : IDecodedSource
{
    private const int Phases = 128, Taps = 128;
    private static readonly ConcurrentDictionary<(int, int, bool), double[][]> Kernels = new();
    private readonly PcmDecoder decoder;
    private readonly double[][] kernel;
    private readonly double[] history;
    private readonly byte[] block;
    private readonly int channels, from, to;
    private int bytesRead, blockPosition, ring;
    private long inputIndex = -1, clock;

    public MinimumPhaseSource(Track track, double offset, int outputRate, bool steep, double inputGain = 1)
    {
        channels = track.Channels;
        from = track.Rate;
        to = outputRate;
        kernel = Kernels.GetOrAdd((from, to, steep), static key => Prepare(key.Item1, key.Item2, key.Item3));
        history = new double[channels * Taps];
        block = new byte[512 * channels * sizeof(double)];
        decoder = new PcmDecoder(track, offset, from, "f64le", inputGain: inputGain);
    }

    private static double[][] Prepare(int from, int to, bool steep)
    {
        const int fftSize = 65536;
        int half = (steep ? 64 : 42) * Phases;
        double cutoff = Math.Min(1, (double)to / from) * (steep ? .97 : .91);
        var fft = new Complex[fftSize];
        for (int i = -half; i <= half; i++)
        {
            double t = (double)i / Phases * cutoff;
            double window = .42 + .5 * Math.Cos(Math.PI * i / half) + .08 * Math.Cos(2 * Math.PI * i / half);
            fft[(i + fftSize) % fftSize] = cutoff / Phases * (t == 0 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t)) * window;
        }
        Fft.Transform(fft);
        for (int i = 0; i < fft.Length; i++)
            fft[i] = Math.Log(Math.Max(1e-10, fft[i].Magnitude));
        Fft.Transform(fft, inverse: true);
        for (int i = 1; i < fft.Length / 2; i++)
            fft[i] *= 2;
        Array.Clear(fft, fft.Length / 2 + 1, fft.Length / 2 - 1);
        Fft.Transform(fft);
        for (int i = 0; i < fft.Length; i++)
            fft[i] = Complex.Exp(fft[i]);
        Fft.Transform(fft, inverse: true);
        var result = new double[Phases][];
        for (int phase = 0; phase < Phases; phase++)
        {
            var row = result[phase] = new double[Taps];
            for (int tap = 0; tap < row.Length; tap++)
                row[tap] = fft[tap * Phases + phase].Real * Phases *
                    (tap < 112 ? 1 : .5 + .5 * Math.Cos(Math.PI * (tap - 112) / 16));
            double sum = row.Sum();
            for (int tap = 0; tap < row.Length; tap++)
                row[tap] /= sum;
        }
        return result;
    }

    public int Read(Span<byte> target)
    {
        int written = 0;
        int stride = channels * sizeof(double);
        while (written + stride <= target.Length)
        {
            long sourceFrame = clock / to;
            while (inputIndex < sourceFrame)
            {
                if (blockPosition == bytesRead)
                {
                    bytesRead = 0;
                    blockPosition = 0;
                    int count;
                    while (bytesRead < block.Length && (count = decoder.Read(block.AsSpan(bytesRead))) != 0)
                        bytesRead += count;
                    if (bytesRead == 0)
                        return written;
                    if (bytesRead % stride != 0)
                        throw new IOException("The resampler received a partial audio frame.");
                }
                ring = (ring + 1) & (Taps - 1);
                for (int channel = 0; channel < channels; channel++, blockPosition += sizeof(double))
                    history[ring * channels + channel] = BinaryPrimitives.ReadDoubleLittleEndian(block.AsSpan(blockPosition));
                inputIndex++;
            }
            var row = kernel[(int)((clock % to) * Phases / to)];
            for (int channel = 0; channel < channels; channel++, written += sizeof(double))
            {
                double sample = 0;
                for (int tap = 0; tap < Taps; tap++)
                    sample += history[((ring - tap) & (Taps - 1)) * channels + channel] * row[tap];
                BinaryPrimitives.WriteDoubleLittleEndian(target[written..], sample);
            }
            clock += from;
        }
        return written;
    }

    public void Cancel() => decoder.Cancel();
    public void Dispose() => decoder.Dispose();
}
