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

internal sealed unsafe class Binaural : IDisposable
{
    public const int Block = 512;
    private const string Dll = "phonon";
    private IntPtr context, hrtf;
    private readonly IntPtr[] effects;
    private readonly string[] channels;
    private readonly float[] input = new float[Block], left = new float[Block], right = new float[Block];

    public Binaural(int rate, string[] channels)
    {
        this.channels = channels;
        effects = new IntPtr[channels.Length];
        try
        {
            var configuration = new ContextSettings { Version = 0x040801, Simd = 3 };
            Check(iplContextCreate(ref configuration, out context));
            var audio = new AudioSettings { Rate = rate, Frames = Block };
            var profile = new HrtfSettings { Volume = 1 };
            Check(iplHRTFCreate(context, ref audio, ref profile, out hrtf));
            for (int i = 0; i < channels.Length; i++)
                if (channels[i] != "LFE")
                    Check(iplBinauralEffectCreate(context, ref audio, ref hrtf, out effects[i]));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Render(double[] source, double[] stereo)
    {
        Array.Clear(stereo);
        float gain = channels.Length > 2 ? .5f : .7071f;
        fixed (float* mono = input, l = left, r = right)
        {
            float** inputPointers = stackalloc float*[1] { mono };
            float** outputPointers = stackalloc float*[2] { l, r };
            var sourceBuffer = new Buffer { Channels = 1, Frames = Block, Data = inputPointers };
            var stereoBuffer = new Buffer { Channels = 2, Frames = Block, Data = outputPointers };
            for (int channel = 0; channel < channels.Length; channel++)
            {
                for (int i = 0; i < Block; i++)
                    input[i] = (float)source[i * channels.Length + channel];
                if (effects[channel] == IntPtr.Zero)
                {
                    for (int i = 0; i < Block; i++)
                    {
                        stereo[i * 2] += input[i] * .25f;
                        stereo[i * 2 + 1] += input[i] * .25f;
                    }
                    continue;
                }
                double angle = Speakers.Angle(channels[channel]) * Math.PI / 180;
                var settings = new EffectParameters
                {
                    X = (float)Math.Sin(angle), Z = -(float)Math.Cos(angle),
                    Interpolation = 1, Blend = 1, Hrtf = hrtf
                };
                iplBinauralEffectApply(effects[channel], ref settings, ref sourceBuffer, ref stereoBuffer);
                for (int i = 0; i < Block; i++)
                {
                    stereo[i * 2] += left[i] * gain;
                    stereo[i * 2 + 1] += right[i] * gain;
                }
            }
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < effects.Length; i++)
            if (effects[i] != IntPtr.Zero)
                iplBinauralEffectRelease(ref effects[i]);
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
}
