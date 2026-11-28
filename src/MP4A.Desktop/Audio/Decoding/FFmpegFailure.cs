namespace Soundroom;

internal sealed class FFmpegFailure : IOException
{
    public Track? Track { get; }

    public FFmpegFailure(string executable, int exitCode, string stderr, Track? track = null)
        : base(Describe(stderr, track?.Codec),
            new IOException($"{executable} exited with code {exitCode}.\n{stderr.Trim()}"))
    {
        Track = track;
    }

    private static string Describe(string error, string? codec)
    {
        bool Has(string text) => error.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Has("matches no streams") || Has("contains no stream") || Has("does not contain any stream") ||
            Has("matches no audio streams"))
            return "The file has no audio stream the current engine can play.";

        string stream = string.IsNullOrWhiteSpace(codec) || codec is "unknown" or "none"
            ? "this audio stream" : $"the {codec.ToUpperInvariant()} audio stream";
        if (Has("no decoder found") || Has("decoder not found") || Has("unknown decoder") ||
            Has("no decoder available") || (Has("decoder (codec ") && Has("not found")))
            return $"The current FFmpeg build has no decoder for {stream}.";

        return $"The current audio engine could not open or decode {stream}.";
    }
}
