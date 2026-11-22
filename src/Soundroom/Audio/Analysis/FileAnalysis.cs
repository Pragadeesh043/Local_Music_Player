using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Soundroom;

internal sealed record TrackAnalysis(double? Lufs, double? TruePeak, double? DynamicRange, double? ReplayGain,
    string Spectrum, string Integrity, double? AlbumLufs = null, double? AlbumReplayGain = null);

internal static partial class FileAnalysis
{
    public static async Task<TrackAnalysis> ScanAsync(Track track)
    {
        string result = await MeasureAsync([track]);
        double? integrated = Number(Integrated().Match(result));
        double? peak = Number(Peak().Match(result));
        double? gain = track.Channels <= 2 ? Number(ReplayGain().Match(await MeasureAsync([track], replayGain: true))) : null;
        return await Task.Run(() => new TrackAnalysis(integrated is > -70 ? integrated : null, peak,
            Number(DynamicRange().Match(result)) ?? EstimateDynamicRange(track), gain, SpectralEstimate(track), Integrity(track),
            track.Analysis?.AlbumLufs, track.Analysis?.AlbumReplayGain));
    }

    private static double? EstimateDynamicRange(Track track)
    {
        using var input = new PcmDecoder(track, 0, track.Rate, "f64le");
        int stride = track.Channels * sizeof(double);
        var raw = new byte[stride * 4096];
        var squares = new double[track.Channels];
        var peak = new double[track.Channels];
        var windows = Enumerable.Range(0, track.Channels).Select(_ => new List<(double Rms, double Peak)>()).ToArray();
        int frames = 0, limit = track.Rate * 3;
        void SaveWindow()
        {
            for (int channel = 0; channel < track.Channels; channel++)
                windows[channel].Add((squares[channel] / frames, peak[channel]));
            Array.Clear(squares);
            Array.Clear(peak);
            frames = 0;
        }
        while (true)
        {
            int total = 0, count;
            while (total < raw.Length && (count = input.Read(raw.AsSpan(total))) > 0)
                total += count;
            for (int start = 0; start + stride <= total; start += stride)
            {
                for (int channel = 0; channel < track.Channels; channel++)
                {
                    double sample = Math.Abs(System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(
                        raw.AsSpan(start + channel * sizeof(double))));
                    squares[channel] += sample * sample;
                    peak[channel] = Math.Max(peak[channel], sample);
                }
                if (++frames == limit)
                    SaveWindow();
            }
            if (total < raw.Length)
                break;
        }
        if (frames > 0)
            SaveWindow();
        var scores = windows.Where(channel => channel.Any(window => window.Rms > 1e-15)).Select(channel =>
        {
            double meanSquare = channel.OrderByDescending(window => window.Rms)
                .Take((channel.Count + 4) / 5).Average(window => window.Rms);
            double topPeak = channel.Select(window => window.Peak).OrderDescending().Skip(channel.Count > 1 ? 1 : 0).First();
            return 20 * Math.Log10(Math.Max(topPeak, 1e-12) / Math.Sqrt(meanSquare));
        }).ToArray();
        return scores.Length == 0 ? null : Math.Max(0, scores.Average());
    }

    public static async Task<(double? Lufs, double? ReplayGain)> ScanAlbumAsync(Track[] tracks)
    {
        if (tracks.Length == 0 || tracks.Length > 100 ||
            tracks.Any(track => track.Channels != tracks[0].Channels || track.Layout != tracks[0].Layout))
            throw new IOException("Album scanning requires up to 100 tracks with the same channel layout.");
        double? loudness = Number(Integrated().Match(await MeasureAsync(tracks)));
        double? gain = tracks[0].Channels <= 2 ? Number(ReplayGain().Match(await MeasureAsync(tracks, replayGain: true))) : null;
        return (loudness is > -70 ? loudness : null, gain);
    }

    private static async Task<string> MeasureAsync(Track[] tracks, bool replayGain = false)
    {
        var args = new List<string> { "-hide_banner", "-nostats", "-nostdin", "-xerror" };
        foreach (Track track in tracks)
        {
            args.AddRange(["-protocol_whitelist", "file,pipe", "-ss", track.Start.ToString("F9", CultureInfo.InvariantCulture)]);
            if (track.CueIndex > 0)
                args.AddRange(["-t", track.Duration.ToString("F9", CultureInfo.InvariantCulture)]);
            args.AddRange(["-i", track.Path]);
        }
        string measure = replayGain ? "aformat=channel_layouts=stereo,replaygain" : "ebur128=peak=true,drmeter";
        if (tracks.Length == 1)
            args.AddRange(["-map", "0:a:0", "-af", measure]);
        else
        {
            string prepare = string.Join(";", tracks.Select((_, i) => $"[{i}:a:0]aresample=48000,asetpts=PTS-STARTPTS[a{i}]"));
            string inputs = string.Concat(tracks.Select((_, i) => $"[a{i}]"));
            args.AddRange(["-filter_complex", $"{prepare};{inputs}concat=n={tracks.Length}:v=0:a=1,{measure}[analysis]",
                "-map", "[analysis]"]);
        }
        args.AddRange(["-vn", "-f", "null", "NUL"]);
        using var proc = Media.Start("ffmpeg", args.ToArray());
        var error = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        string result = await error;
        if (proc.ExitCode != 0)
            throw new IOException(result.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim());
        return result;
    }

    private static string SpectralEstimate(Track track)
    {
        if (track.Rate < 88200 || track.Bits < 16)
            return "High-resolution spectral check is for PCM at 88.2 kHz and above.";
        const int size = 4096;
        double[] power = new double[size / 2];
        byte[] raw = new byte[size * track.Channels * sizeof(double)];
        var samples = new double[size * track.Channels];
        var fft = new Complex[size];
        int windows = 0;
        foreach (double fraction in new[] { .1, .3, .5, .7, .9 })
        {
            using var decoder = new PcmDecoder(track, track.Duration * fraction, track.Rate, "f64le");
            for (int block = 0; block < 8; block++)
            {
                int read = 0, total = 0;
                while (total < raw.Length && (read = decoder.Read(raw.AsSpan(total))) != 0)
                    total += read;
                if (total != raw.Length)
                    break;
                Buffer.BlockCopy(raw, 0, samples, 0, raw.Length);
                for (int channel = 0; channel < Math.Min(2, track.Channels); channel++)
                {
                    for (int frame = 0; frame < size; frame++)
                        fft[frame] = samples[frame * track.Channels + channel] * (.5 - .5 * Math.Cos(2 * Math.PI * frame / size));
                    Fft.Transform(fft);
                    for (int bin = 0; bin < power.Length; bin++)
                        power[bin] += fft[bin].Magnitude * fft[bin].Magnitude;
                }
                windows++;
            }
        }
        if (windows < 4)
            return "Insufficient audio for spectral check.";
        double reference = power.Skip((int)(1000.0 * size / track.Rate)).Take((int)(14000.0 * size / track.Rate)).Max();
        if (reference < 1e-12)
            return "Insufficient signal for spectral check.";
        foreach (int cutoff in new[] { 16000, 20000, 22050, 24000, 32000 })
        {
            int bin = (int)((double)cutoff * size / track.Rate);
            int width = Math.Max(1, (int)(1000.0 * size / track.Rate));
            if (bin + width * 2 >= power.Length)
                continue;
            double below = power.Skip(bin - width).Take(width).Max();
            double above = power.Skip(bin + width / 2).Max();
            if (below > reference * 1e-6 && above < below * 1e-4)
                return $"Sharp roll-off near {cutoff / 1000.0:0.##} kHz. Possible bandwidth limit or upsampling.";
        }
        return "No sharp cutoff found in the sampled windows.";
    }

    public static string Integrity(Track track)
    {
        using var file = File.OpenRead(track.Path);
        if (track.Codec != "flac")
            return $"File SHA-256 {Convert.ToHexString(SHA256.HashData(file))}";
        Span<byte> header = stackalloc byte[42];
        file.ReadExactly(header);
        if (!header[..4].SequenceEqual("fLaC"u8) || (header[4] & 0x7f) != 0 ||
            (header[5] << 16 | header[6] << 8 | header[7]) != 34)
            return "FLAC STREAMINFO MD5 is unavailable in this container.";
        var expected = header[26..42];
        if (expected.IndexOfAnyExcept((byte)0) < 0 || track.Bits is < 4 or > 32)
            return "FLAC STREAMINFO MD5 is unavailable.";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var audio = new PcmDecoder(track with { Start = 0, CueIndex = 0 }, 0, track.Rate, "s32le");
        var raw = new byte[65536];
        var canonical = new byte[raw.Length];
        int bytes = (track.Bits + 7) / 8;
        while (true)
        {
            int count = 0, read;
            while (count < raw.Length && (read = audio.Read(raw.AsSpan(count))) > 0)
                count += read;
            if (count == 0)
                break;
            if (count % 4 != 0)
                throw new IOException("FLAC verification received a partial PCM sample.");
            for (int i = 0, sample = 0; i < count; i += 4, sample++)
            {
                int value = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i)) >> (32 - track.Bits);
                for (int b = 0; b < bytes; b++)
                    canonical[sample * bytes + b] = (byte)(value >> (8 * b));
            }
            hash.AppendData(canonical.AsSpan(0, count / 4 * bytes));
        }
        return expected.SequenceEqual(hash.GetHashAndReset()) ? $"FLAC PCM MD5 verified {Convert.ToHexString(expected)}" :
            "FLAC PCM MD5 mismatch.";
    }

    private static double? Number(Match match) => match.Success &&
        double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;

    [GeneratedRegex(@"Integrated loudness:\s+I:\s+([-\d.]+) LUFS")]
    private static partial Regex Integrated();
    [GeneratedRegex(@"True peak:\s+Peak:\s+([-\d.]+) dBFS")]
    private static partial Regex Peak();
    [GeneratedRegex(@"Overall DR:\s+([-\d.]+)")]
    private static partial Regex DynamicRange();
    [GeneratedRegex(@"track_gain\s*=\s*([+\-\d.]+) dB")]
    private static partial Regex ReplayGain();
}
