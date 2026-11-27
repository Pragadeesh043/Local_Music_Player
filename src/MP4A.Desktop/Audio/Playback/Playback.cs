using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundroom;

internal enum Route { Binaural, Downmix, Cable, Exclusive, ExclusiveDsp, Dop, NativeDsd }
internal sealed record OutputDevice(string Id, string Name, int Rate, int Channels, bool HardwareVolume = false, int MixBits = 32)
{
    public override string ToString() => Name;
}
internal readonly record struct Meter(float Peak, float Rms, long Clips);
internal sealed record Signal(Meter[] Source, Meter[] Output, float[] Waveform, int WaveChannel,
    double? TruePeak = null, double? Lufs = null, double? Correlation = null, PhasePoint[]? Phase = null,
    double? LimiterReductionDb = null);
internal sealed record ProcessingControls(DspSettings Dsp, float DigitalVolume);
internal sealed record ProcessingSnapshot(ProcessingControls Controls, double NormalizationDb);

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
    private LiveAnalysis? live;
    private int analysisEnabled = 1;
    private readonly ChannelMeter[] sourceMeters, outputMeters;
    private int measuredFrames;
    private readonly int meterInterval;
    private double intervalPeak;
    private double? intervalLimiterReduction;
    private int remaining, bufferedOffset;
    private int waveChannel;
    private ProcessingControls controls;
    private ProcessingSnapshot? producedProcessing, deliveredProcessing;
    private Signal? signal;
    private Signal? pcmPacketSignal;
    private ProcessingSnapshot? pcmPacketProcessing;
    private bool disposed;
    private long framesRead;
    private long framesPrepared;
    private long binauralSourceFrames;
    private long mediaEndFrame = long.MaxValue;
    private int binauralTailRemaining = -1;
    private long consumedBoundary;
    private Track consumedTrack;
    private readonly ConcurrentQueue<Track> consumedTransitions = new();
    private readonly Func<bool>? canAdvance;
    private int endedForOrder;
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
    private readonly int pauseFadeMilliseconds;

    public Playback(Track track, OutputDevice device, Route route, double offset,
        DspSettings settings, float volume, PlaybackOptions? options = null)
    {
        options ??= new();
        pauseFadeMilliseconds = options.PauseFadeMilliseconds;
        this.route = route;
        canAdvance = options.CanAdvance;
        controls = new(settings, volume);
        this.offset = offset;
        consumedTrack = track;
        Device = device;
        MinimumPhase = options.MinimumPhase;
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
        if (route is not (Route.Binaural or Route.Downmix) && Speakers.HasHeight(SourceChannels))
            throw new NotSupportedException("Choose Binaural headphones or Stereo downmix for this height layout.");
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
            direct = DirectFormat.Match(client, request, rate, route == Route.ExclusiveDsp ? ["FL", "FR"] : OutputChannels);
        }
        WaveFormat = direct?.Format ?? (route == Route.Cable
            ? DirectFormat.Extensible(rate, 8, 32, 32, 0x63f, floating: true)
            : WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
        source = new double[Block * track.Channels];
        input = new byte[Block * (IsRaw ? WaveFormat.BlockAlign : track.Channels * sizeof(double))];
        packed = new byte[Block * WaveFormat.BlockAlign];
        rendered = new double[Block * OutputChannels.Length];
        sourceMeters = new ChannelMeter[track.Channels];
        outputMeters = new ChannelMeter[OutputChannels.Length];
        meterInterval = Math.Max(1, rate / 30);
        ImpulsePath = IsProcessed ? options.ImpulsePath : null;
        dsp = new StereoDsp(rate, ImpulsePath is not null ? ImpulseResponse.Load(ImpulsePath, rate) : null);
        var resampler = IsRaw ? null : options.ResamplingFilter(rate);
        if (route == Route.Binaural)
            binaural = new Binaural(rate, SourceChannels);
        decoder = new AudioSource(track, offset, rate, IsRaw ? direct!.Encoding : "f64le",
            resampler, options.Memory, options.Following, route, WaveFormat.BitsPerSample,
            options.MinimumPhase, options.Sharper, options.Preparation, options.NextTrack, options.MemoryLimitMiB, canAdvance);
    }

    public string[] SourceChannels { get; }
    public string[] OutputChannels { get; }
    public WaveFormat WaveFormat { get; }
    public PlaybackState State => output?.PlaybackState ?? PlaybackState.Stopped;
    public Signal? Latest => Volatile.Read(ref signal);
    public Track CurrentTrack => audioBuffer is null ? decoder.Current : Volatile.Read(ref consumedTrack);
    public bool TryTakeTransition([NotNullWhen(true)] out Track? item) => consumedTransitions.TryDequeue(out item);
    public double Position
    {
        get
        {
            long boundary = audioBuffer is null ? decoder.Boundary : Interlocked.Read(ref consumedBoundary);
            long mediaFrames = Math.Min(Interlocked.Read(ref framesRead), Interlocked.Read(ref mediaEndFrame));
            return Math.Max(0, (double)(mediaFrames - boundary) / rate + (boundary == 0 ? offset : 0));
        }
    }
    public int Underruns => Volatile.Read(ref underruns);
    public bool NeedsScanBackpressure => State == PlaybackState.Playing && Volatile.Read(ref completed) == 0 &&
        audioBuffer is { BelowScanReserve: true };
    private bool IsRaw => route == Route.Exclusive || dop;
    public int OutputBits => direct?.ValidBits ?? 32;
    public bool IsExclusive => exclusive;
    internal Route OutputRoute => route;
    internal OutputDevice Device { get; }
    internal string? AsioDriver => asioDriver;
    internal bool MinimumPhase { get; }
    internal string? ImpulsePath { get; }
    internal bool IsProcessed => route is Route.Binaural or Route.Downmix or Route.ExclusiveDsp;
    internal double? ReadAsioSampleRate() => (output as AsioDsdOut)?.DriverSampleRate();
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
        if (!dop)
        {
            output = new PcmWasapiOut(device.Id, exclusive, OutputBits, pauseFadeMilliseconds);
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
        var error = args.Exception ?? (Volatile.Read(ref endedForOrder) == 0 ? Volatile.Read(ref decoderFailure) : null);
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

    // The unread decoder ring is the continuation of the stream. The endpoint's rendered
    // packets are drained and reset independently, so resume does not seek or skip media.
    internal bool CanPrimePcm(int frames) => Volatile.Read(ref completed) != 0 ||
        Interlocked.Read(ref framesPrepared) - Interlocked.Read(ref framesRead) >= frames;

    public void Play()
    {
        if (output is null)
            return;
        primed.Task.GetAwaiter().GetResult();
        output.Play();
    }
    public Task ResumeAsync()
    {
        if (output is PcmWasapiOut pcm)
            return pcm.PlayAsync();
        output?.Play();
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        if (output is PcmWasapiOut pcm)
            return pcm.PauseAsync();
        output?.Pause();
        return Task.CompletedTask;
    }
    public void Update(DspSettings updated, float newVolume)
    {
        var previous = Volatile.Read(ref controls);
        if (!ReferenceEquals(previous.Dsp, updated) || previous.DigitalVolume != newVolume)
            Volatile.Write(ref controls, new ProcessingControls(updated, newVolume));
    }

    internal ProcessingSnapshot ReadProcessing(out bool applied, out bool pending)
    {
        var requested = Volatile.Read(ref controls);
        var delivered = Volatile.Read(ref deliveredProcessing);
        applied = delivered is not null;
        pending = delivered is not null && !ReferenceEquals(delivered.Controls, requested);
        return delivered ?? new ProcessingSnapshot(requested,
            IsProcessed && requested.Dsp.Enabled ? requested.Dsp.NormalizationGain(CurrentTrack) : 0);
    }
    public void Inspect(int channel) => Volatile.Write(ref waveChannel, Math.Clamp(channel, 0, SourceChannels.Length - 1));
    public void SetAnalysisEnabled(bool enabled) => Volatile.Write(ref analysisEnabled, enabled ? 1 : 0);

    internal void ConfirmPcmPacket()
    {
        Volatile.Write(ref signal, pcmPacketSignal);
        Volatile.Write(ref deliveredProcessing, pcmPacketProcessing);
    }

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
        Volatile.Write(ref deliveredProcessing, producedProcessing);
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
            while (Volatile.Read(ref stopping) == 0 && Volatile.Read(ref endedForOrder) == 0)
            {
                if (queue.Writable() is not { } block)
                {
                    Thread.Sleep(State == PlaybackState.Playing ? 3 : 20);
                    continue;
                }
                block.Transitions.Clear();
                if (!Fill(block.Transitions, publishEveryBlock: false))
                    break;
                Buffer.BlockCopy(IsRaw ? input : packed, 0, block.Data, 0, remaining);
                block.Length = remaining;
                block.Signal = producedSignal;
                block.Processing = producedProcessing;
                queue.Publish();
                Interlocked.Add(ref framesPrepared, block.Length / WaveFormat.BlockAlign);
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
        if (Volatile.Read(ref stopping) != 0 || Volatile.Read(ref endedForOrder) != 0 || (IsRaw && Underruns != 0))
            return 0;
        bool pcm = output is PcmWasapiOut;
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
            if (transitionIndex < block.Transitions.Count && canAdvance?.Invoke() == false)
            {
                long framesToBoundary = block.Transitions[transitionIndex].Frame - Interlocked.Read(ref framesRead);
                if (framesToBoundary == 0)
                {
                    Volatile.Write(ref endedForOrder, 1);
                    break;
                }
                take = (int)Math.Min(take, framesToBoundary * WaveFormat.BlockAlign);
            }
            Buffer.BlockCopy(block.Data, bufferedPosition, buffer, offset + written, take);
            written += take;
            bufferedPosition += take;
            long delivered = Interlocked.Add(ref framesRead, take / WaveFormat.BlockAlign);
            while (transitionIndex < block.Transitions.Count && block.Transitions[transitionIndex].Frame < delivered)
            {
                var transition = block.Transitions[transitionIndex++];
                Interlocked.Exchange(ref consumedBoundary, transition.Frame);
                Volatile.Write(ref consumedTrack, transition.Track);
                consumedTransitions.Enqueue(transition.Track);
            }
            if (pcm)
            {
                pcmPacketSignal = block.Signal;
                pcmPacketProcessing = block.Processing;
            }
            else
            {
                Volatile.Write(ref signal, block.Signal);
                Volatile.Write(ref deliveredProcessing, block.Processing);
            }
            if (bufferedPosition == block.Length)
            {
                bufferedPosition = transitionIndex = 0;
                queue.Release();
            }
        }
        return written;
    }

    private bool Fill(List<(Track Track, long Frame)>? transitions = null, bool publishEveryBlock = true)
    {
        int prefetchWaitMs = audioBuffer is not { } queue ? -1 : State != PlaybackState.Playing ? 500 :
            (int)Math.Clamp((long)queue.Queued * Block * 1000 / rate - 100, 0, 125);
        int read = binauralTailRemaining < 0 ? decoder.Read(input, transitions, prefetchWaitMs) : 0;
        int sourceFrames = read / (IsRaw ? WaveFormat.BlockAlign : sizeof(double) * SourceChannels.Length);
        int frames = binaural is null ? sourceFrames : BinauralOutputFrames(sourceFrames);
        if (frames == 0)
            return false;
        var current = Volatile.Read(ref controls);
        var currentSettings = current.Dsp;
        double normalizationDb = IsProcessed && currentSettings.Enabled
            ? currentSettings.NormalizationGain(decoder.Current) : 0;
        if (producedProcessing is null || !ReferenceEquals(producedProcessing.Controls, current) ||
            producedProcessing.NormalizationDb != normalizationDb)
            producedProcessing = new(current, normalizationDb);
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
            Buffer.BlockCopy(input, 0, source, 0, sourceFrames * SourceChannels.Length * sizeof(double));
        if (binaural is not null)
            binaural.Render(source, rendered);
        else if (!IsRaw)
            Mix(frames);
        if (IsProcessed)
            dsp.Apply(rendered, frames, currentSettings, current.DigitalVolume, normalizationDb);
        if (!IsRaw)
        {
            int count = frames * OutputChannels.Length;
            if (direct is null)
                PackFloat(rendered.AsSpan(0, count), MemoryMarshal.Cast<byte, float>(packed.AsSpan()));
            else
            {
                int bytes = WaveFormat.BitsPerSample / 8;
                for (int i = 0; i < count; i++)
                {
                    var target = packed.AsSpan(i * bytes, bytes);
                    quantizer.Write(target, rendered[i], direct.ValidBits, currentSettings.Dither, i % 2);
                }
            }
        }

        remaining = frames * WaveFormat.BlockAlign;
        bufferedOffset = 0;
        if (Volatile.Read(ref analysisEnabled) == 0)
        {
            if (live is not null)
            {
                live = null;
                producedSignal = null;
                measuredFrames = 0;
                intervalPeak = 0;
                intervalLimiterReduction = null;
                ResetMeterWindow(sourceMeters);
                ResetMeterWindow(outputMeters);
            }
            return remaining != 0;
        }
        live ??= new(rate, OutputChannels);
        Accumulate(source, frames, sourceMeters);
        Accumulate(rendered, frames, outputMeters);
        measuredFrames += frames;
        var stats = live.Measure(rendered, frames);
        intervalPeak = Math.Max(intervalPeak, stats.TruePeak);
        if (IsProcessed && dsp.LimiterReductionDb is double reduction)
            intervalLimiterReduction = Math.Max(intervalLimiterReduction ?? 0, reduction);
        if (publishEveryBlock || producedSignal is null || measuredFrames >= meterInterval || read < input.Length)
        {
            int channel = Volatile.Read(ref waveChannel);
            var waveform = new float[frames];
            for (int i = 0; i < frames; i++)
                waveform[i] = (float)source[i * SourceChannels.Length + channel];
            producedSignal = new Signal(Snapshot(sourceMeters, measuredFrames), Snapshot(outputMeters, measuredFrames),
                waveform, channel, intervalPeak, stats.Lufs, stats.Correlation, live.Scope(rendered, frames), intervalLimiterReduction);
            measuredFrames = 0;
            intervalPeak = 0;
            intervalLimiterReduction = null;
        }
        return remaining != 0;
    }

    private int BinauralOutputFrames(int sourceFrames)
    {
        binauralSourceFrames += sourceFrames;
        if (sourceFrames == Block)
            return Block;

        if (binauralTailRemaining < 0)
        {
            Interlocked.Exchange(ref mediaEndFrame, binauralSourceFrames);
            binauralTailRemaining = binauralSourceFrames > 0 && canAdvance?.Invoke() != false
                ? binaural!.TailFrames : 0;
        }

        // Fill the final partial DSP block before advancing the convolution to another block.
        int tail = Math.Min(Block - sourceFrames, binauralTailRemaining);
        binauralTailRemaining -= tail;
        return sourceFrames + tail;
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

    private static void PackFloat(ReadOnlySpan<double> input, Span<float> output)
    {
        int index = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int half = Vector<double>.Count;
            for (; index <= input.Length - Vector<float>.Count; index += Vector<float>.Count)
                Vector.Narrow(new Vector<double>(input[index..]), new Vector<double>(input[(index + half)..]))
                    .CopyTo(output[index..]);
        }
        for (; index < input.Length; index++)
            output[index] = (float)input[index];
    }

    private struct ChannelMeter
    {
        public float Peak;
        public double Sum;
        public long Clips;
    }

    private static void Accumulate(double[] data, int frames, ChannelMeter[] meters)
    {
        for (int channel = 0; channel < meters.Length; channel++)
        {
            ref var meter = ref meters[channel];
            for (int frame = 0; frame < frames; frame++)
            {
                double value = Math.Abs(data[frame * meters.Length + channel]);
                meter.Peak = Math.Max(meter.Peak, (float)value);
                meter.Sum += value * value;
                if (value > 1)
                    meter.Clips++;
            }
        }
    }

    private static Meter[] Snapshot(ChannelMeter[] meters, int frames)
    {
        var snapshot = new Meter[meters.Length];
        for (int channel = 0; channel < meters.Length; channel++)
        {
            ref var meter = ref meters[channel];
            snapshot[channel] = new(meter.Peak, (float)Math.Sqrt(meter.Sum / frames), meter.Clips);
            meter.Peak = 0;
            meter.Sum = 0;
        }
        return snapshot;
    }

    private static void ResetMeterWindow(ChannelMeter[] meters)
    {
        foreach (ref var meter in meters.AsSpan())
        {
            meter.Peak = 0;
            meter.Sum = 0;
        }
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
    }
}
