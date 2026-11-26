using System.Text.RegularExpressions;

namespace Soundroom;

internal static partial class CueSheet
{
    private sealed record Entry(string File, int Number, string Title, string Artist, double Start);

    public static async Task<Track[]> ReadAsync(string path)
    {
        string directory = Path.GetDirectoryName(path)!;
        var entries = new List<Entry>();
        string? audioFile = null;
        string album = "", albumArtist = "", title = "", artist = "";
        int number = 0;
        bool isAudio = false;
        foreach (string raw in await File.ReadAllLinesAsync(path))
        {
            string line = raw.Trim();
            int split = line.IndexOf(' ');
            if (split < 0)
                continue;
            string data = line[(split + 1)..].Trim();
            switch (line[..split].ToUpperInvariant())
            {
                case "FILE":
                    var match = Filename().Match(data);
                    if (!match.Success)
                        throw new IOException("Invalid FILE entry in CUE sheet.");
                    audioFile = Path.GetFullPath(Path.Combine(directory, match.Groups[1].Value.Trim('"')));
                    if (!File.Exists(audioFile) || !Media.Extensions.Contains(Path.GetExtension(audioFile), StringComparer.OrdinalIgnoreCase))
                        throw new IOException($"CUE audio file unavailable: {audioFile}");
                    break;
                case "TRACK":
                    title = artist = "";
                    var fields = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    number = fields.Length > 0 && int.TryParse(fields[0], out int value) ? value : 0;
                    isAudio = fields.Length == 2 && fields[1].Equals("AUDIO", StringComparison.OrdinalIgnoreCase);
                    break;
                case "TITLE":
                    if (number == 0) album = data.Trim('"');
                    else title = data.Trim('"');
                    break;
                case "PERFORMER":
                    if (number == 0) albumArtist = data.Trim('"');
                    else artist = data.Trim('"');
                    break;
                case "INDEX" when isAudio && number > 0 && audioFile is not null:
                    var index = Index().Match(data);
                    if (!index.Success)
                        break;
                    int m = int.Parse(index.Groups[1].Value), s = int.Parse(index.Groups[2].Value), f = int.Parse(index.Groups[3].Value);
                    if (s >= 60 || f >= 75)
                        throw new IOException("Invalid CUE INDEX 01 time.");
                    entries.Add(new(audioFile, number, title, artist, 60 * m + s + f / 75.0));
                    break;
            }
        }
        var sources = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        var tracks = new List<Track>();
        for (int i = 0; i < entries.Count; i++)
        {
            var cue = entries[i];
            if (!sources.TryGetValue(cue.File, out var source))
                sources[cue.File] = source = await Media.ProbeAsync(cue.File);
            double end = i + 1 < entries.Count && entries[i + 1].File.Equals(cue.File, StringComparison.OrdinalIgnoreCase)
                ? entries[i + 1].Start : source.Duration;
            if (cue.Start >= end || end > source.Duration + .01)
                throw new IOException($"CUE track {cue.Number} has an invalid time range.");
            var tags = new Dictionary<string, string>(source.Tags, StringComparer.OrdinalIgnoreCase)
            {
                ["track"] = cue.Number.ToString(), ["cuesheet"] = path
            };
            tracks.Add(source with
            {
                Title = cue.Title.Length > 0 ? cue.Title : $"Track {cue.Number}",
                Artist = cue.Artist.Length > 0 ? cue.Artist : albumArtist.Length > 0 ? albumArtist : source.Artist,
                Album = album.Length > 0 ? album : source.Album, Tags = tags,
                Start = cue.Start, CueIndex = cue.Number, Duration = end - cue.Start
            });
        }
        if (tracks.Count == 0)
            throw new IOException("No indexed audio tracks were found in the CUE sheet.");
        return tracks.ToArray();
    }

    [GeneratedRegex("^(\"[^\"]+\"|.+?)\\s+\\w+$")]
    private static partial Regex Filename();
    [GeneratedRegex(@"^01\s+(\d+):(\d{2}):(\d{2})$")]
    private static partial Regex Index();
}
