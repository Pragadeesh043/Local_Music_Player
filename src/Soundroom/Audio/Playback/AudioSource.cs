namespace Soundroom;

internal sealed record PlaybackOptions(bool Memory = false, int TargetRate = 0, int OutputBits = 24,
    bool Sharper = false, IReadOnlyList<Track>? Following = null, string? ImpulsePath = null,
    bool MinimumPhase = false, string? AsioDriver = null, CancellationToken Preparation = default)
{
    public string ResamplingFilter(int rate) =>
        $"aresample={rate}:resampler=swr:tsf=dblp:filter_type=kaiser:kaiser_beta=12:filter_size={(Sharper ? 256 : 128)}:phase_shift=12:exact_rational=1:cutoff={(Sharper ? "0.97" : "0.91")}";
}

internal interface IDecodedSource : IDisposable
{
    int Read(Span<byte> buffer);
    void Cancel();
}

internal sealed class AudioSource : IDisposable
{
    private readonly object gate = new();
    private readonly int alignment;
    private readonly int bytesPerSample;
    private readonly Route route;
    private readonly Queue<Track> following;
    private readonly Func<Track, double, CancellationToken, IDecodedSource> open;
    private readonly CancellationTokenSource prefetchStop = new();
    private readonly CancellationToken prefetchToken;
    private IDecodedSource source;
    private IDecodedSource? pending;
    private Task? prefetchTask;
    private Track current;
    private Track? next;
    private bool canceled;
    private long total, boundary;

    public AudioSource(Track track, double offset, int rate, string encoding, string? resampler,
        bool memory, IReadOnlyList<Track>? queue, Route route, int bits, bool minimumPhase = false, bool sharper = false,
        CancellationToken preparation = default)
    {
        this.route = route;
        prefetchToken = prefetchStop.Token;
        current = track;
        bytesPerSample = encoding switch { "s16le" => 2, "s24le" => 3, "s32le" => 4, _ => 8 };
        alignment = track.Channels * bytesPerSample;
        following = new(queue ?? []);
        open = (item, seconds, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            IDecodedSource decoded = (route is Route.Dop or Route.NativeDsd) ? new DsdReader(item, seconds, bits) :
                minimumPhase && encoding == "f64le" && item.Rate != rate ? new MinimumPhaseSource(item, seconds, rate, sharper) :
                new PcmDecoder(item, seconds, rate, encoding, resampler);
            if (!memory)
                return decoded;
            using (decoded)
            using (cancellation.Register(() => Diagnostics.Cleanup("Canceling RAM preload", decoded.Cancel)))
                return new MemoryAudioSource(decoded, cancellation);
        };
        try { source = open(track, offset, preparation); }
        catch { prefetchStop.Dispose(); throw; }
        Prefetch();
    }

    public Track Current => Volatile.Read(ref current);
    public long Boundary => Interlocked.Read(ref boundary);

    private void Prefetch()
    {
        if (!following.TryDequeue(out Track? track) || track.Channels != current.Channels || track.Layout != current.Layout ||
            ((route is Route.Exclusive or Route.ExclusiveDsp or Route.Dop or Route.NativeDsd) && track.Rate != current.Rate) ||
            (route == Route.Exclusive && (track.Bits != current.Bits || track.Codec != current.Codec)) ||
            ((route is Route.Dop or Route.NativeDsd) && track.Bits != 1))
            return;
        prefetchTask = Task.Run(() =>
        {
            try
            {
                var decoded = open(track, 0, prefetchToken);
                lock (gate)
                {
                    if (canceled)
                        decoded.Dispose();
                    else
                    {
                        pending = decoded;
                        next = track;
                    }
                }
            }
            catch (OperationCanceledException) when (prefetchToken.IsCancellationRequested) { }
            catch (Exception error) { Diagnostics.Write($"Prefetching {track.Title}", error); }
        });
    }

    public int Read(byte[] buffer, List<(Track Track, long Frame)>? transitions = null)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            int count = source.Read(buffer.AsSpan(filled));
            if (route == Route.Dop)
            {
                for (int i = 0; i + alignment <= count; i += alignment)
                {
                    byte marker = (((total + i) / alignment) & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                    for (int at = i + bytesPerSample - 1; at < i + alignment; at += bytesPerSample)
                        buffer[filled + at] = marker;
                }
            }
            total += count;
            filled += count;
            if (count != 0)
                continue;
            // Only the decode worker waits here. The device can continue draining queued audio.
            prefetchTask?.GetAwaiter().GetResult();
            lock (gate)
            {
                if (canceled || pending is null || next is null)
                    break;
                source.Dispose();
                source = pending;
                pending = null;
                Interlocked.Exchange(ref boundary, total / alignment);
                Volatile.Write(ref current, next);
                transitions?.Add((next, total / alignment));
                next = null;
                Prefetch();
            }
        }
        return filled;
    }

    public void Cancel()
    {
        lock (gate)
        {
            if (canceled)
                return;
            canceled = true;
            prefetchStop.Cancel();
            source.Cancel();
            pending?.Dispose();
            pending = null;
        }
    }

    public void Dispose()
    {
        try { Cancel(); }
        finally
        {
            source.Dispose();
            prefetchStop.Dispose();
        }
    }
}

internal sealed class MemoryAudioSource : IDecodedSource
{
    private readonly MemoryStream stream;
    public MemoryAudioSource(IDecodedSource input, CancellationToken cancellation = default)
    {
        const int limit = 256 * 1024 * 1024;
        using var data = new MemoryStream();
        byte[] buffer = new byte[65536];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            if (data.Length + count > limit)
                throw new IOException("This track exceeds the 256 MiB RAM playback limit.");
            data.Write(buffer, 0, count);
        }
        cancellation.ThrowIfCancellationRequested();
        stream = new MemoryStream(data.GetBuffer(), 0, (int)data.Length, writable: false);
    }

    public int Read(Span<byte> buffer) => stream.Read(buffer);
    public void Cancel() { }
    public void Dispose() => stream.Dispose();
}
