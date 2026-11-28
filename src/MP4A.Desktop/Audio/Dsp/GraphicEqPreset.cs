namespace Soundroom;

internal sealed record GraphicEqPreset(string Id, string Label, double[] Gains, double TrimDb)
{
    public static readonly GraphicEqPreset[] All =
    [
        new("flat", "Flat / Reference / Neutral", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 0),
        new("mid-hump", "Inverted U / Mid-Hump", [0, 0, .5, 1.5, 2, 1.5, .5, 0, 0, 0], -4.5),
        new("warm-tilt", "Warm Tilt", [3, 2.5, 2, 1.5, .5, 0, -.5, -1.5, -2, -2.5], -5.5),
        new("bright-tilt", "Bright Tilt", [-3, -2.5, -2, -1.5, -.5, 0, .5, 1.5, 2, 2.5], -4.5),
        new("telephone", "Telephone / Bandpass", [-6, -4, -2, 0, 0, 0, 0, 0, -2, -4], 0),
        new("bass-boost", "Bass Boost / Sub-Bass", [5, 5, 3.5, 1, 0, 0, 0, 0, 0, 0], -8.5),
        new("bass-reducer", "Bass Reducer", [-5, -4, -2.5, -.5, 0, 0, 0, 0, 0, 0], 0),
        new("treble-boost", "Treble Boost / Air", [0, 0, 0, 0, 0, 0, 0, 1, 1.5, 2.5], -4.5),
        new("treble-reducer", "Treble Reducer / Dark", [0, 0, 0, 0, 0, 0, 0, -1, -3, -2], 0),
        new("vocal-booster", "Vocal Booster / Presence", [0, 0, 0, 0, .5, 1.5, 2.5, 1.5, .5, 0], -5),
        new("warm-full", "Warm / Full / Rich", [0, .5, 1.5, 2, .5, 0, 0, -.5, -1.5, -2], -4),
        new("scooped", "Scooped / Hollow", [0, 0, 0, -.5, -4, -4, -1, 0, 0, 0], 0),
        new("v", "V", [4, 3, 2, 0, -2, -3, -2, 0, 2, 4], -6.5),
        new("m", "M", [-2, 0, 3, 2, 0, -2, 0, 3, 2, -2], -5),
        new("w", "W", [4, 3, 0, -2, 0, 3, 0, -2, 2, 4], -6.5),
        new("u", "U", [3, 3, 2, 0, -1, -1, -1, 0, 2, 3], -5.5)
    ];

    public bool IsFlat => Id == "flat";
    public bool Telephone => Id == "telephone";

    public static GraphicEqPreset? Find(string? id) => id is null ? null : Array.Find(All, preset => preset.Id == id);
}
