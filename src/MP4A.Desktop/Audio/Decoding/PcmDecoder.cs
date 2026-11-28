using System.Diagnostics;
using System.Globalization;

namespace Soundroom;

internal sealed class PcmDecoder : IDecodedSource
{
    private readonly Process process;
    private readonly Task<string> stderr;
    private readonly Stream stream;
    private readonly Track track;
    private int canceled;
    private int disposed;

    public PcmDecoder(Track track, double offset, int rate, string encoding, string? resampler = null, double inputGain = 1)
    {
        this.track = track;
        var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-protocol_whitelist", "file,pipe",
            "-ss", (track.Start + offset).ToString("F9", CultureInfo.InvariantCulture), "-i", track.Path,
            "-map", "0:a:0", "-vn", "-sn", "-dn" };
        if (track.CueIndex > 0)
            args.AddRange(["-t", Math.Max(0, track.Duration - offset).ToString("F9", CultureInfo.InvariantCulture)]);
        if (inputGain != 1)
        {
            // Scale in floating point at the input rate before running the rate-conversion filter.
            string gain = $"aresample=osf=dbl:tsf=dblp:rematrix_volume={inputGain.ToString("R", CultureInfo.InvariantCulture)}";
            resampler = resampler is null ? gain : $"{gain},{resampler}";
        }
        if (resampler is not null)
            args.AddRange(["-af", resampler]);
        args.AddRange(["-ar", rate.ToString(CultureInfo.InvariantCulture), "-c:a", $"pcm_{encoding}", "-f", encoding, "pipe:1"]);
        process = Media.Start("ffmpeg", args.ToArray());
        stderr = process.StandardError.ReadToEndAsync();
        stream = process.StandardOutput.BaseStream;
    }

    public int Read(Span<byte> target)
    {
        int read = stream.Read(target);
        if (read == 0 && Volatile.Read(ref canceled) == 0)
        {
            process.WaitForExit();
            if (Volatile.Read(ref canceled) == 0 && process.ExitCode != 0)
                throw new FFmpegFailure(process.StartInfo.FileName, process.ExitCode, stderr.GetAwaiter().GetResult(), track);
        }
        return read;
    }

    public void Cancel()
    {
        if (Interlocked.Exchange(ref canceled, 1) != 0)
            return;
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        Diagnostics.Cleanup("Stopping decoder process", Cancel);
        Diagnostics.Cleanup("Closing decoder stream", stream.Dispose);
        process.Dispose();
    }
}
