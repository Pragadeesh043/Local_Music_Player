using System.Diagnostics;
using System.Globalization;

namespace Soundroom;

internal sealed class PcmDecoder : IDecodedSource
{
    private readonly Process process;
    private readonly Task<string> stderr;
    private readonly Stream stream;
    private int canceled;
    private int disposed;

    public PcmDecoder(Track track, double offset, int rate, string encoding, string? resampler = null)
    {
        var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-protocol_whitelist", "file,pipe",
            "-ss", (track.Start + offset).ToString("F9", CultureInfo.InvariantCulture), "-i", track.Path,
            "-map", "0:a:0", "-vn", "-sn", "-dn" };
        if (track.CueIndex > 0)
            args.AddRange(["-t", Math.Max(0, track.Duration - offset).ToString("F9", CultureInfo.InvariantCulture)]);
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
            if (process.ExitCode != 0)
                throw Media.ToolFailure("ffmpeg", process.ExitCode, stderr.GetAwaiter().GetResult());
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
