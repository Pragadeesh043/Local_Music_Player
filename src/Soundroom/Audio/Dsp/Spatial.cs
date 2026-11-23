using System.Runtime.InteropServices;

namespace Soundroom;

internal static class Speakers
{
    public static readonly string[] Surround71 = ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"];
    public static string[] From(Track track) => (track.Channels, track.Layout) switch
    {
        (1, _) => ["FC"],
        (2, "" or "stereo") => ["FL", "FR"],
        (3, "2.1") => ["FL", "FR", "LFE"],
        (3, "3.0") => ["FL", "FR", "FC"],
        (4, "quad") => ["FL", "FR", "BL", "BR"],
        (4, "quad(side)") => ["FL", "FR", "SL", "SR"],
        (5, "5.0") => ["FL", "FR", "FC", "BL", "BR"],
        (5, "5.0(side)") => ["FL", "FR", "FC", "SL", "SR"],
        (6, "5.1") => ["FL", "FR", "FC", "LFE", "BL", "BR"],
        (6, "5.1(side)") => ["FL", "FR", "FC", "LFE", "SL", "SR"],
        (8, "7.1") => Surround71.ToArray(),
        _ => Enumerable.Range(1, track.Channels).Select(i => $"Ch {i}").ToArray()
    };

    public static double Angle(string speaker) => speaker switch
    {
        "FL" => -30, "FR" => 30, "SL" => -95, "SR" => 95,
        "BL" => -145, "BR" => 145, _ => 0
    };
}

internal sealed unsafe class HrtfResponseReader : IDisposable
{
    private const int Block = Binaural.Block;
    private const string Dll = "phonon";
    private IntPtr context, hrtf;
    private IntPtr effect;

    public HrtfResponseReader(int rate)
    {
        try
        {
            var configuration = new ContextSettings { Version = 0x040801, Simd = 3 };
            Check(iplContextCreate(ref configuration, out context));
            var audio = new AudioSettings { Rate = rate, Frames = Block };
            var profile = new HrtfSettings { Volume = 1 };
            Check(iplHRTFCreate(context, ref audio, ref profile, out hrtf));
            Check(iplBinauralEffectCreate(context, ref audio, ref hrtf, out effect));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public ImpulseResponse Read(double degrees)
    {
        var input = new float[Block];
        var left = new float[Block];
        var right = new float[Block];
        input[0] = 1;
        var responseLeft = new List<double>();
        var responseRight = new List<double>();
        fixed (float* mono = input, l = left, r = right)
        {
            float** inputPointers = stackalloc float*[1] { mono };
            float** outputPointers = stackalloc float*[2] { l, r };
            var source = new Buffer { Channels = 1, Frames = Block, Data = inputPointers };
            var stereo = new Buffer { Channels = 2, Frames = Block, Data = outputPointers };
            double angle = degrees * Math.PI / 180;
            var settings = new EffectParameters
            {
                X = (float)Math.Sin(angle), Z = -(float)Math.Cos(angle),
                Interpolation = 1, Blend = 1, Hrtf = hrtf
            };
            iplBinauralEffectApply(effect, ref settings, ref source, ref stereo);
            for (int i = 0; i < Block; i++)
            {
                responseLeft.Add(left[i]);
                responseRight.Add(right[i]);
            }
            int tail = iplBinauralEffectGetTailSize(effect);
            if (tail > 16384)
                throw new InvalidOperationException("Unexpectedly long binaural impulse response.");
            while (tail > 0)
            {
                iplBinauralEffectGetTail(effect, ref stereo);
                int frames = Math.Min(tail, Block);
                for (int i = 0; i < frames; i++)
                {
                    responseLeft.Add(left[i]);
                    responseRight.Add(right[i]);
                }
                tail -= frames;
            }
        }
        if (!responseLeft.All(double.IsFinite) || !responseRight.All(double.IsFinite) ||
            !responseLeft.Any(sample => Math.Abs(sample) > 1e-8) ||
            !responseRight.Any(sample => Math.Abs(sample) > 1e-8))
            throw new InvalidOperationException("Steam Audio did not return a usable binaural impulse response.");
        return new(responseLeft.ToArray(), responseRight.ToArray());
    }

    public void Dispose()
    {
        if (effect != IntPtr.Zero)
            iplBinauralEffectRelease(ref effect);
        if (hrtf != IntPtr.Zero)
            iplHRTFRelease(ref hrtf);
        if (context != IntPtr.Zero)
            iplContextRelease(ref context);
    }

    private static void Check(int code)
    {
        if (code != 0)
            throw new InvalidOperationException($"Steam Audio initialization failed ({code}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ContextSettings { public uint Version; public IntPtr Log, Allocate, Free; public int Simd, Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioSettings { public int Rate, Frames; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HrtfSettings { public int Type; public IntPtr Name, Data; public int Size; public float Volume; public int Norm; }
    [StructLayout(LayoutKind.Sequential)]
    private struct EffectParameters { public float X, Y, Z; public int Interpolation; public float Blend; public IntPtr Hrtf, Delays; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Buffer { public int Channels, Frames; public float** Data; }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int iplContextCreate(ref ContextSettings settings, out IntPtr result);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void iplContextRelease(ref IntPtr context);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int iplHRTFCreate(IntPtr context, ref AudioSettings audio, ref HrtfSettings settings, out IntPtr result);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void iplHRTFRelease(ref IntPtr hrtf);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int iplBinauralEffectCreate(IntPtr context, ref AudioSettings audio, ref IntPtr hrtf, out IntPtr result);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int iplBinauralEffectApply(IntPtr effect, ref EffectParameters settings, ref Buffer input, ref Buffer output);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void iplBinauralEffectRelease(ref IntPtr effect);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int iplBinauralEffectGetTailSize(IntPtr effect);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int iplBinauralEffectGetTail(IntPtr effect, ref Buffer output);

}

internal sealed class Binaural
{
    public const int Block = 512;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Rate, double Angle), ImpulseResponse> Responses = new();
    private readonly StereoConvolution?[] filters;
    private readonly double[] scratch = new double[Block * 2];
    private readonly double gain;

    public Binaural(int rate, string[] channels)
    {
        filters = channels.Select(channel => channel == "LFE" ? null : new StereoConvolution(
            Responses.GetOrAdd((rate, Speakers.Angle(channel)), static key =>
            {
                using var reader = new HrtfResponseReader(key.Rate);
                return reader.Read(key.Angle);
            }))).ToArray();
        gain = channels.Length > 2 ? .5 : .7071;
    }

    public void Render(double[] source, double[] stereo)
    {
        Array.Clear(stereo);
        for (int channel = 0; channel < filters.Length; channel++)
        {
            var filter = filters[channel];
            if (filter is null)
            {
                for (int frame = 0; frame < Block; frame++)
                {
                    double bass = source[frame * filters.Length + channel] * .25;
                    stereo[frame * 2] += bass;
                    stereo[frame * 2 + 1] += bass;
                }
                continue;
            }
            for (int frame = 0; frame < Block; frame++)
                scratch[frame * 2] = scratch[frame * 2 + 1] = source[frame * filters.Length + channel];
            filter.Apply(scratch, Block);
            for (int sample = 0; sample < stereo.Length; sample++)
                stereo[sample] += scratch[sample] * gain;
        }
    }

}
