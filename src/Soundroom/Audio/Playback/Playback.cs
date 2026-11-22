using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundroom;

internal enum Route { Binaural, Downmix, Cable, Exclusive, ExclusiveDsp, Dop, NativeDsd }
internal sealed record OutputDevice(string Id, string Name, int Rate, int Channels, bool HardwareVolume = false, int MixBits = 32)
{
    public override string ToString() => Name;
}
internal sealed record Meter(float Peak, float Rms, long Clips);
internal sealed record Signal(Meter[] Source, Meter[] Output, float[] Waveform, int WaveChannel,
    double? TruePeak = null, double? Lufs = null, double? Correlation = null);

internal sealed class Playback : IWaveProvider, IDisposable
{
    private const int Block = Binaural.Block;
    private readonly AudioSource decoder;
    private readonly Binaural? binaural;
    private readonly StereoDsp dsp;
    private readonly Route route;
    private readonly int[] mapping;
    private readonly double[] source, rendered;
    private readonly byte[] input, packed;
    private readonly DirectFormat? direct;
    private readonly bool dop;
    private readonly Quantizer quantizer = new();
    private readonly bool exclusive;
    private readonly string? asioDriver;
    private readonly LiveAnalysis? live;
    private readonly long[] sourceClips, outputClips;
    private int remaining, bufferedOffset;
    private int waveChannel;
    private DspSettings settings;
    private float volume;
    private Signal? signal;
    private bool disposed;
    private long framesRead;
    private long consumedBoundary;
    private Track consumedTrack;
    private int bufferedPosition, transitionIndex;
    private AudioBuffer? audioBuffer;
    private Thread? producer;
    private readonly TaskCompletionSource primed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? decoderFailure;
    private int completed, stopping, underruns;
    private Signal? producedSignal;
    private readonly double offset;
    private readonly int rate;
    private IWavePlayer? output;

    public Playback(Track track, OutputDevice device, Route route, double offset,
        DspSettings settings, float volume, PlaybackOptions? options = null)
    {
        options ??= new();
        this.route = route;
        this.settings = settings;
        this.volume = volume;
        this.offset = offset;
        consumedTrack = track;
        asioDriver = route == Route.NativeDsd ? options.AsioDriver ??
            throw new InvalidOperationException("Select a DSD-capable ASIO driver in Output settings.") : null;
        exclusive = route is Route.Exclusive or Route.ExclusiveDsp or Route.Dop or Route.NativeDsd;
        dop = route is Route.Dop or Route.NativeDsd;
        rate = dop ? DsdReader.CarrierRate(track) : route switch
        {
            Route.Exclusive => track.Rate,
            Route.ExclusiveDsp => options.TargetRate > 0 ? options.TargetRate : track.Rate,
            Route.Binaural => 48000,
            _ => device.Rate
        };
        if (track.Channels is < 1 or > 32)
            throw new NotSupportedException($"Unsupported channel count: {track.Channels}.");
        SourceChannels = Speakers.From(track);
        bool unmapped = SourceChannels.Any(x => x.StartsWith("Ch ", StringComparison.Ordinal));
        if (unmapped && route != Route.Downmix)
            throw new NotSupportedException("This layout has no known speaker mapping. Choose Stereo downmix.");
        if (route == Route.Cable && device.Channels != 8)
            throw new InvalidOperationException("Choose an output configured for 7.1 in Windows Sound settings.");
        OutputChannels = route == Route.Cable ? Speakers.Surround71 :
            route == Route.Exclusive || dop ? SourceChannels : ["L", "R"];
        mapping = SourceChannels.Select(channel => Array.IndexOf(OutputChannels, channel)).ToArray();
        if (asioDriver is not null)
            direct = new(new WaveFormat(rate, 24, track.Channels), 24, "s24le");
        else if (exclusive)
        {
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDevice(device.Id);
            using var client = endpoint.AudioClient;
            var request = route == Route.ExclusiveDsp ? track with { Codec = "pcm_s24le", Bits = options.OutputBits, Channels = 2 } : track;
            if (dop)
                request = track with { Codec = "pcm_s24le", Bits = 24 };
            direct = DirectFormat.Match(client, request, rate, OutputChannels);
        }
        WaveFormat = direct?.Format ?? (route == Route.Cable
            ? DirectFormat.Extensible(rate, 8, 32, 32, 0x63f, floating: true)
            : WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
        source = new double[Block * track.Channels];
        input = new byte[Block * (IsRaw ? WaveFormat.BlockAlign : track.Channels * sizeof(double))];
        packed = new byte[Block * WaveFormat.BlockAlign];
        rendered = new double[Block * OutputChannels.Length];
        sourceClips = new long[track.Channels];
        outputClips = new long[OutputChannels.Length];
        dsp = new StereoDsp(rate, options.ImpulsePath is not null && (route is Route.Downmix or Route.Binaural or Route.ExclusiveDsp)
            ? ImpulseResponse.Load(options.ImpulsePath, rate) : null);
        if (!dop)
            live = new LiveAnalysis(rate, OutputChannels);
        var resampler = IsRaw ? null : options.ResamplingFilter(rate);
        if (route == Route.Binaural)
            binaural = new Binaural(rate, SourceChannels);
        try
        {
            decoder = new AudioSource(track, offset, rate, IsRaw ? direct!.Encoding : "f64le",
                resampler, options.Memory, options.Following, route, WaveFormat.BitsPerSample,
                options.MinimumPhase, options.Sharper, options.Preparation);
        }
        catch
        {
            binaural?.Dispose();
            throw;
        }
    }

    public string[] SourceChannels { get; }
    public string[] OutputChannels { get; }
    public WaveFormat WaveFormat { get; }
    public PlaybackState State => output?.PlaybackState ?? PlaybackState.Stopped;
    public Signal? Latest => Volatile.Read(ref signal);
    public Track CurrentTrack => audioBuffer is null ? decoder.Current : Volatile.Read(ref consumedTrack);
    public double Position
    {
        get
        {
            long boundary = audioBuffer is null ? decoder.Boundary : Interlocked.Read(ref consumedBoundary);
            return Math.Max(0, (double)(Interlocked.Read(ref framesRead) - boundary) / rate + (boundary == 0 ? offset : 0));
        }
    }
    public int Underruns => Volatile.Read(ref underruns);
    private bool IsRaw => route == Route.Exclusive || dop;
    public int OutputBits => direct?.ValidBits ?? 32;
    public bool IsExclusive => exclusive;
    public event EventHandler<StoppedEventArgs>? Stopped;

    public static List<OutputDevice> Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            using var primary = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = primary.ID;
        }
        var result = new List<OutputDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    using var client = device.AudioClient;
                    bool hardware = false;
                    try
                    {
                        using var level = device.AudioEndpointVolume;
                        hardware = level.HardwareSupport.HasFlag(EEndpointHardwareSupport.Volume);
                    }
                    catch (COMException) { }
                    var mix = client.MixFormat;
                    result.Add(new(device.ID, device.FriendlyName, mix.SampleRate, mix.Channels, hardware, mix.BitsPerSample));
                }
                catch (COMException) { }
            }
        }
        return result.OrderByDescending(device => device.Id == defaultId).ToList();
    }

    public void Open(OutputDevice device, IntPtr window = default)
    {
        if (asioDriver is not null)
        {
            output = new AsioDsdOut(asioDriver, window);
            output.PlaybackStopped += OutputStopped;
            output.Init(this);
            StartProducer();
            return;
        }
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDevice(device.Id);
        var mode = exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
        if (exclusive && !endpoint.AudioClient.IsFormatSupported(mode, WaveFormat))
            throw new NotSupportedException("The requested exclusive format is no longer available.");
        output = new WasapiOut(endpoint, mode, true, 100);
        output.PlaybackStopped += OutputStopped;
        output.Init(this);
        if (exclusive && !FormatBytes(WaveFormat).SequenceEqual(FormatBytes(output.OutputWaveFormat)))
            throw new NotSupportedException("The audio driver changed the requested exclusive format.");
        StartProducer();
    }

    private void OutputStopped(object? sender, StoppedEventArgs args)
    {
        var error = args.Exception ?? Volatile.Read(ref decoderFailure);
        if (error is null && IsRaw && Underruns > 0)
            error = new IOException("The source stopped supplying audio in time. Press Play to resume.");
        Stopped?.Invoke(this, error is null ? args : new StoppedEventArgs(error));
    }

    private static byte[] FormatBytes(WaveFormat format)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        format.Serialize(writer);
        return stream.ToArray();
    }

    public Task WaitUntilBufferedAsync(CancellationToken cancellation) => primed.Task.WaitAsync(cancellation);

    public void Play()
    {
        if (output is null)
            return;
        primed.Task.GetAwaiter().GetResult();
        output.Play();
    }
    public void Pause() => output?.Pause();
    public void Update(DspSettings updated, float newVolume) { Volatile.Write(ref settings, updated); Volatile.Write(ref volume, newVolume); }
    public void Inspect(int channel) => Volatile.Write(ref waveChannel, Math.Clamp(channel, 0, SourceChannels.Length - 1));

    public int Read(byte[] buffer, int offset, int count)
    {
        if (audioBuffer is { } queue)
            return ReadBuffered(queue, buffer, offset, count);

        // Direct pulls are used for decoder verification without opening a hardware output.
        int written = 0;
        while (written < count)
        {
            if (remaining == 0 && !Fill())
                break;
            int take = Math.Min(remaining, count - written);
            Buffer.BlockCopy(IsRaw ? input : packed, bufferedOffset, buffer, offset + written, take);
            bufferedOffset += take;
            remaining -= take;
            written += take;
        }
        Interlocked.Add(ref framesRead, written / WaveFormat.BlockAlign);
        Volatile.Write(ref signal, producedSignal);
        return written;
    }

    private void StartProducer()
    {
        int blockBytes = Block * WaveFormat.BlockAlign;
        int blocks = Math.Clamp((int)Math.Ceiling(rate * .5 / Block), 16, (16 * 1024 * 1024) / blockBytes);
        var queue = audioBuffer = new AudioBuffer(blocks, blockBytes);
        producer = new Thread(() => Produce(queue, blocks))
        {
            IsBackground = true, Name = "Soundroom audio worker", Priority = ThreadPriority.AboveNormal
        };
        producer.Start();
    }

    private void Produce(AudioBuffer queue, int startupBlocks)
    {
        try
        {
            while (Volatile.Read(ref stopping) == 0)
            {
                if (queue.Writable() is not { } block)
                {
                    Thread.Sleep(State == PlaybackState.Playing ? 3 : 20);
                    continue;
                }
                block.Transitions.Clear();
                if (!Fill(block.Transitions))
                    break;
                Buffer.BlockCopy(IsRaw ? input : packed, 0, block.Data, 0, remaining);
                block.Length = remaining;
                block.Signal = producedSignal;
                queue.Publish();
                if (queue.Queued >= startupBlocks)
                    primed.TrySetResult();
            }
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref stopping) == 0)
            {
                Volatile.Write(ref decoderFailure, error);
                if (queue.Queued == 0)
                    primed.TrySetException(error);
            }
        }
        finally
        {
            Volatile.Write(ref completed, 1);
            if (Volatile.Read(ref stopping) != 0)
                primed.TrySetCanceled();
            else
                primed.TrySetResult();
        }
    }

    private int ReadBuffered(AudioBuffer queue, byte[] buffer, int offset, int count)
    {
        if (Volatile.Read(ref stopping) != 0 || (IsRaw && Underruns != 0))
            return 0;
        int written = 0;
        while (written < count)
        {
            if (queue.Readable() is not { } block)
            {
                if (Volatile.Read(ref completed) != 0 || Volatile.Read(ref stopping) != 0)
                {
                    // The final publication can race the first empty observation.
                    if (Volatile.Read(ref stopping) == 0 && queue.Readable() is not null)
                        continue;
                    break;
                }
                Interlocked.Increment(ref underruns);
                if (IsRaw)
                    break;
                Array.Clear(buffer, offset + written, count - written);
                return count;
            }
            int take = Math.Min(count - written, block.Length - bufferedPosition);
            Buffer.BlockCopy(block.Data, bufferedPosition, buffer, offset + written, take);
            written += take;
            bufferedPosition += take;
            long delivered = Interlocked.Add(ref framesRead, take / WaveFormat.BlockAlign);
            while (transitionIndex < block.Transitions.Count && block.Transitions[transitionIndex].Frame < delivered)
            {
                var transition = block.Transitions[transitionIndex++];
                Interlocked.Exchange(ref consumedBoundary, transition.Frame);
                Volatile.Write(ref consumedTrack, transition.Track);
            }
            Volatile.Write(ref signal, block.Signal);
            if (bufferedPosition == block.Length)
            {
                bufferedPosition = transitionIndex = 0;
                queue.Release();
            }
        }
        return written;
    }

    private bool Fill(List<(Track Track, long Frame)>? transitions = null)
    {
        int read = decoder.Read(input, transitions);
        if (read == 0)
            return false;
        int frames = read / (IsRaw ? WaveFormat.BlockAlign : sizeof(double) * SourceChannels.Length);
        if (dop)
        {
            remaining = frames * WaveFormat.BlockAlign;
            bufferedOffset = 0;
            return remaining != 0;
        }
        Array.Clear(source);
        if (IsRaw)
        {
            int bytes = WaveFormat.BitsPerSample / 8;
            for (int i = 0; i < frames * SourceChannels.Length; i++)
                source[i] = Quantizer.Decode(input.AsSpan(i * bytes, bytes));
            Array.Copy(source, rendered, source.Length);
        }
        else
            Buffer.BlockCopy(input, 0, source, 0, frames * SourceChannels.Length * sizeof(double));
        if (binaural is not null)
            binaural.Render(source, rendered);
        else if (!IsRaw)
            Mix(frames);
        var currentSettings = Volatile.Read(ref settings);
        if (route is Route.Binaural or Route.Downmix or Route.ExclusiveDsp)
            dsp.Apply(rendered, frames, currentSettings, Volatile.Read(ref volume), decoder.Current);
        if (!IsRaw)
        {
            int bytes = WaveFormat.BitsPerSample / 8;
            for (int i = 0; i < frames * OutputChannels.Length; i++)
            {
                var target = packed.AsSpan(i * bytes, bytes);
                if (direct is null)
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(target, (float)rendered[i]);
                else
                    quantizer.Write(target, rendered[i], direct.ValidBits, currentSettings.Dither, i % 2);
            }
        }

        int channel = Volatile.Read(ref waveChannel);
        var waveform = new float[frames];
        for (int i = 0; i < frames; i++)
            waveform[i] = (float)source[i * SourceChannels.Length + channel];
        var stats = live?.Measure(rendered, frames);
        producedSignal = new Signal(
            Measure(source, SourceChannels.Length, frames, sourceClips),
            Measure(rendered, OutputChannels.Length, frames, outputClips), waveform, channel,
            stats?.TruePeak, stats?.Lufs, stats?.Correlation);
        remaining = frames * WaveFormat.BlockAlign;
        bufferedOffset = 0;
        return remaining != 0;
    }

    private void Mix(int frames)
    {
        Array.Clear(rendered);
        int inputs = SourceChannels.Length;
        int outputs = OutputChannels.Length;
        if (route == Route.Cable)
        {
            for (int i = 0; i < frames; i++)
                for (int channel = 0; channel < inputs; channel++)
                    if (mapping[channel] >= 0)
                        rendered[i * outputs + mapping[channel]] = source[i * inputs + channel];
            return;
        }
        float normalization = inputs > 2 ? .5f : 1;
        for (int i = 0; i < frames; i++)
        {
            for (int channel = 0; channel < inputs; channel++)
            {
                var (left, right) = SourceChannels[channel] switch
                {
                    "FL" => (1f, 0f), "FR" => (0f, 1f),
                    "SL" or "BL" => (.7071f, 0f), "SR" or "BR" => (0f, .7071f),
                    "LFE" => (.35f, .35f), "FC" when inputs == 1 => (1f, 1f),
                    _ => (.7071f, .7071f)
                };
                double sample = source[i * inputs + channel] * normalization;
                rendered[i * 2] += left * sample;
                rendered[i * 2 + 1] += right * sample;
            }
        }
    }

    private static Meter[] Measure(double[] data, int channels, int frames, long[] clips)
    {
        var meters = new Meter[channels];
        for (int channel = 0; channel < channels; channel++)
        {
            float peak = 0;
            double sum = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                double value = Math.Abs(data[frame * channels + channel]);
                peak = Math.Max(peak, (float)value);
                sum += value * value;
                if (value > 1)
                    clips[channel]++;
            }
            meters[channel] = new(peak, (float)Math.Sqrt(sum / Math.Max(1, frames)), clips[channel]);
        }
        return meters;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Volatile.Write(ref stopping, 1);
        Diagnostics.Cleanup("Canceling audio decode", decoder.Cancel);
        if (output is not null)
            Diagnostics.Cleanup("Closing audio output", output.Dispose);
        producer?.Join();
        Diagnostics.Cleanup("Releasing audio decoder", decoder.Dispose);
        if (binaural is not null)
            Diagnostics.Cleanup("Releasing binaural renderer", binaural.Dispose);
    }
}
