using System.Globalization;
using System.Text.RegularExpressions;

namespace Soundroom;

internal static partial class AutoEq
{
    public static DspSettings Read(string text)
    {
        var preamp = Preamp().Match(text);
        var bands = new List<EqBand>();
        foreach (Match filter in Band().Matches(text))
        {
            var shape = filter.Groups[2].Value.ToUpperInvariant() switch
            {
                "PK" => FilterShape.Peak,
                "LS" or "LSC" => FilterShape.LowShelf,
                "HS" or "HSC" => FilterShape.HighShelf,
                "LP" or "LPQ" => FilterShape.LowPass,
                "HP" or "HPQ" => FilterShape.HighPass,
                _ => throw new NotSupportedException($"Unsupported filter type {filter.Groups[2].Value}.")
            };
            bands.Add(new(Parse(filter.Groups[3]), filter.Groups[4].Success ? Parse(filter.Groups[4]) : 0,
                filter.Groups[5].Success ? Parse(filter.Groups[5]) : .7071, shape, EqChannel.Both,
                filter.Groups[1].Value.Equals("ON", StringComparison.OrdinalIgnoreCase)));
        }
        if (bands.Count is < 1 or > 64)
            throw new IOException("The preset must contain 1–64 supported parametric filters.");
        return DspSettings.Flat() with { Preamp = preamp.Success ? Parse(preamp.Groups[1]) : 0, Parametric = bands.ToArray() };
    }

    private static double Parse(Group group) => double.Parse(group.Value, CultureInfo.InvariantCulture);
    [GeneratedRegex(@"(?im)^\s*Preamp:\s*([+\-\d.]+)\s*dB\s*$")]
    private static partial Regex Preamp();
    [GeneratedRegex(@"(?im)^\s*Filter\s+\d+:\s+(ON|OFF)\s+(\w+)\s+Fc\s+([\d.]+)\s*Hz(?:\s+Gain\s+([+\-\d.]+)\s*dB)?(?:\s+Q\s+([\d.]+))?\s*$")]
    private static partial Regex Band();
}
