namespace Soundroom;

internal sealed class TrackSequence
{
    private readonly string[] keys;
    private readonly Dictionary<string, int> positions;

    public TrackSequence(string[] keys, string anchor, bool shuffle)
    {
        this.keys = keys;
        if (shuffle)
        {
            int first = Array.FindIndex(keys, key => key.Equals(anchor, StringComparison.OrdinalIgnoreCase));
            if (first >= 0)
                (keys[first], keys[0]) = (keys[0], keys[first]);
            Random.Shared.Shuffle(keys.AsSpan(first >= 0 ? 1 : 0));
        }
        positions = new(keys.Length, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < keys.Length; index++)
            positions.Add(keys[index], index);
    }

    public IEnumerable<string> After(string anchor, bool backwards = false)
    {
        int step = backwards ? -1 : 1;
        int index = positions.TryGetValue(anchor, out int found) ? found : (backwards ? 0 : -1);
        for (index += step; index >= 0 && index < keys.Length; index += step)
            yield return keys[index];
    }
}
