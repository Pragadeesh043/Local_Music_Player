using System.Text.Json;

namespace Soundroom;

public sealed partial class MainWindow
{
    private object ReadDsp()
    {
        int rate = playback?.WaveFormat.SampleRate ?? 48000;
        double[] Response(EqChannel channel)
        {
            var filters = dsp.Filters(rate, channel);
            return Enumerable.Range(0, 120).Select(index =>
                dsp.Enabled ? dsp.Preamp + filters.Sum(filter => filter.Response(20 * Math.Pow(1000, index / 119.0), rate)) : 0).ToArray();
        }
        return new { settings = dsp with { ImpulsePath = null }, impulse = Path.GetFileName(dsp.ImpulsePath),
            presets = library.Presets().Keys.Order().ToArray(), left = Response(EqChannel.Left), right = Response(EqChannel.Right),
            bypassed = playback is { IsProcessed: false } };
    }

    private async Task<object> EditDsp(JsonElement args)
    {
        string action = WebText(args, "action");
        string name = WebText(args, "name").Trim();
        switch (action)
        {
            case "apply":
                var settings = args.GetProperty("settings").Deserialize<DspSettings>(WebJson)
                    ?? throw new InvalidOperationException("Invalid DSP settings.");
                ValidateDsp(settings);
                await ApplyDsp(settings with { ImpulsePath = dsp.ImpulsePath });
                break;
            case "reset":
                await ApplyDsp(DspSettings.Flat());
                break;
            case "load":
                if (!library.Presets().TryGetValue(name, out var preset))
                    throw new InvalidOperationException("That DSP preset is unavailable.");
                await ApplyDsp(preset);
                break;
            case "save":
                if (name.Length == 0)
                    throw new InvalidOperationException("Enter a name for the DSP preset.");
                library.SavePreset(name, dsp);
                status = $"Saved {name}.";
                break;
            case "autoeq":
                if (await PickFile(".txt") is { } eq)
                {
                    var imported = AutoEq.Read(await File.ReadAllTextAsync(eq.Path));
                    await ApplyDsp(imported);
                    status = $"Imported {imported.Parametric.Length} EQ bands from {eq.Name}.";
                }
                break;
            case "impulse":
                if (await PickFile(".wav") is { } wav)
                    await ApplyDsp(dsp with { ImpulsePath = wav.Path });
                break;
            case "clearImpulse":
                await ApplyDsp(dsp with { ImpulsePath = null });
                break;
            default:
                throw new InvalidOperationException("Unknown DSP action.");
        }
        return ReadDsp();
    }

    private static void ValidateDsp(DspSettings settings)
    {
        static bool Range(double value, double minimum, double maximum) => value >= minimum && value <= maximum;
        if (!Range(settings.Preamp, -24, 6) || !Range(settings.Bass, -12, 12) || !Range(settings.Width, 0, 200) ||
            !Range(settings.Crossfeed, 0, 100) || !Range(settings.Balance, -1, 1) || !Range(settings.Headroom, 0, 12) ||
            !Enum.IsDefined(settings.Dither) || !Enum.IsDefined(settings.Normalization) ||
            settings.Graphic is null || settings.Graphic.Length != DspSettings.Centers.Length ||
            settings.Graphic.Any(value => !Range(value, -12, 12)) ||
            settings.Parametric is null || settings.Parametric.Length > 64 || settings.Parametric.Any(band =>
                band is null || !Enum.IsDefined(band.Shape) || !Enum.IsDefined(band.Channel) ||
                !Range(band.Frequency, 20, 22000) || !Range(band.Gain, -30, 24) || !Range(band.Q, .05, 30)))
            throw new InvalidOperationException("One or more DSP controls are out of range.");
    }

    private async Task ApplyDsp(DspSettings settings)
    {
        if (!ready)
            return;
        bool impulseChanged = dsp.ImpulsePath != settings.ImpulsePath;
        dsp = settings;
        if (impulseChanged)
            await RestartPlaybackAsync();
        else
            playback?.Update(dsp, DigitalVolume());
    }
}
