namespace Soundroom;

internal sealed record PlaybackOptions(bool Memory = false, int TargetRate = 0, int OutputBits = 24,
    bool Sharper = false, IReadOnlyList<Track>? Following = null, string? ImpulsePath = null,
    bool MinimumPhase = false, string? AsioDriver = null, CancellationToken Preparation = default,
    Func<Track, Track?>? NextTrack = null, int MemoryLimitMiB = RamPlaybackBudget.DefaultLimitMiB,
    Func<bool>? CanAdvance = null, int PauseFadeMilliseconds = 400)
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
    private readonly Func<Track, Track?>? nextTrack;
    private readonly Func<bool>? canAdvance;
    private readonly Func<Track, double, CancellationToken, IDecodedSource> open;
    private readonly CancellationTokenSource prefetchStop = new();
    private readonly CancellationToken prefetchToken;
    private IDecodedSource source;
    private IDecodedSource? pending;
    private Task? prefetchTask;
    private Track current;
    private Track? next;
    private bool canceled, prefetchExpired;
    private long total, boundary;

    public AudioSource(Track track, double offset, int rate, string encoding, string? resampler,
        bool memory, IReadOnlyList<Track>? queue, Route route, int bits, bool minimumPhase = false, bool sharper = false,
        CancellationToken preparation = default, Func<Track, Track?>? nextTrack = null,
        int memoryLimitMiB = RamPlaybackBudget.DefaultLimitMiB, Func<bool>? canAdvance = null)
    {
        this.route = route;
        prefetchToken = prefetchStop.Token;
        current = track;
        bytesPerSample = encoding switch { "s16le" => 2, "s24le" => 3, "s32le" => 4, _ => 8 };
        alignment = track.Channels * bytesPerSample;
        var budget = new RamPlaybackBudget(memoryLimitMiB);
        following = new(queue ?? []);
        this.nextTrack = nextTrack;
        this.canAdvance = canAdvance;
        IDecodedSource OpenDecoder(Track item, double seconds) =>
            (route is Route.Dop or Route.NativeDsd) ? new DsdReader(item, seconds, bits) :
                minimumPhase && encoding == "f64le" && item.Rate != rate ? new MinimumPhaseSource(item, seconds, rate, sharper) :
                new PcmDecoder(item, seconds, rate, encoding, resampler);
        open = (item, seconds, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            var decoded = OpenDecoder(item, seconds);
            if (!memory || (item.Duration - seconds) * rate * alignment > budget.Available)
                return decoded;
            MemoryAudioSource? cached;
            using (decoded)
            using (cancellation.Register(() => Diagnostics.Cleanup("Canceling RAM preload", decoded.Cancel)))
                cached = MemoryAudioSource.TryLoad(decoded, budget, alignment, cancellation);
            if (cancellation.IsCancellationRequested)
                cached?.Dispose();
            cancellation.ThrowIfCancellationRequested();
            return cached is not null ? cached : OpenDecoder(item, seconds);
        };
        try { source = open(track, offset, preparation); }
        catch { prefetchStop.Dispose(); throw; }
        Prefetch();
    }

    public Track Current => Volatile.Read(ref current);
    public long Boundary => Interlocked.Read(ref boundary);

    private void Prefetch()
    {
        if (canAdvance?.Invoke() == false)
            return;
        if (!following.TryDequeue(out Track? queued) && nextTrack is null)
            return;
        Track previous = current;
        prefetchTask = Task.Run(() =>
        {
            Track? track = queued;
            try
            {
                prefetchToken.ThrowIfCancellationRequested();
                track ??= nextTrack?.Invoke(previous);
                if (track is null || track.Channels != previous.Channels || track.Layout != previous.Layout ||
                    ((route is Route.Exclusive or Route.ExclusiveDsp or Route.Dop or Route.NativeDsd) && track.Rate != previous.Rate) ||
                    (route == Route.Exclusive && (track.Bits != previous.Bits || track.Codec != previous.Codec)) ||
                    ((route is Route.Dop or Route.NativeDsd) && track.Bits != 1))
                    return;
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
            catch (Exception error) { Diagnostics.Write($"Prefetching after {previous.Title}", error); }
        });
    }

    public int Read(byte[] buffer, List<(Track Track, long Frame)>? transitions = null, int prefetchWaitMs = -1)
    {
        if (prefetchExpired)
            return 0;
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
            if (canAdvance?.Invoke() == false)
                break;
            // Preserve queued audio if the next decoder cannot become ready before the device needs it.
            if (prefetchTask is { IsCompleted: false } task && !task.Wait(prefetchWaitMs, prefetchToken))
            {
                prefetchExpired = true;
                break;
            }
            lock (gate)
            {
                if (canceled || pending is null || next is null || canAdvance?.Invoke() == false)
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

internal sealed class RamPlaybackBudget(int limitMiB)
{
    public const int DefaultLimitMiB = 256;
    public const int MinimumLimitMiB = 16;
    public const int MaximumLimitMiB = 1024;
    private readonly int limit = Math.Clamp(limitMiB, MinimumLimitMiB, MaximumLimitMiB) * 1024 * 1024;
    private int used;
    public int Available => limit - Volatile.Read(ref used);

    // AudioSource preloads only one track at a time. Playback can release the current track concurrently.
    public void Reserve(int bytes) => Interlocked.Add(ref used, bytes);
    public void Release(int bytes) => Interlocked.Add(ref used, -bytes);
}

internal sealed class MemoryAudioSource(List<byte[]> chunks, int length, int chunkSize,
    RamPlaybackBudget budget, int reserved) : IDecodedSource
{
    private int position;
    private int reservedBytes = reserved;

    public static MemoryAudioSource? TryLoad(IDecodedSource input, RamPlaybackBudget budget, int alignment,
        CancellationToken cancellation)
    {
        int chunkSize = 64 * 1024 / alignment * alignment;
        int limit = budget.Available / alignment * alignment;
        var blocks = new List<byte[]>();
        int total = 0;
        while (total < limit)
        {
            cancellation.ThrowIfCancellationRequested();
            var chunk = GC.AllocateUninitializedArray<byte>(Math.Min(chunkSize, limit - total), pinned: true);
            int filled = 0;
            while (filled < chunk.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                int count = input.Read(chunk.AsSpan(filled));
                if (count == 0)
                {
                    if (filled > 0)
                        blocks.Add(chunk);
                    return Finish(total + filled, total + (filled > 0 ? chunk.Length : 0));
                }
                filled += count;
            }
            blocks.Add(chunk);
            total += filled;
        }
        cancellation.ThrowIfCancellationRequested();
        Span<byte> overflow = stackalloc byte[alignment];
        return input.Read(overflow) == 0 ? Finish(total, total) : null;

        MemoryAudioSource Finish(int bytes, int allocated)
        {
            cancellation.ThrowIfCancellationRequested();
            var cached = new MemoryAudioSource(blocks, bytes, chunkSize, budget, allocated);
            budget.Reserve(allocated);
            return cached;
        }
    }

    public int Read(Span<byte> buffer)
    {
        int written = 0;
        int available = Math.Min(buffer.Length, length - position);
        while (written < available)
        {
            int offset = position % chunkSize;
            int count = Math.Min(available - written, chunkSize - offset);
            chunks[position / chunkSize].AsSpan(offset, count).CopyTo(buffer[written..]);
            position += count;
            written += count;
        }
        return written;
    }
    public void Cancel() { }
    public void Dispose()
    {
        chunks.Clear();
        budget.Release(Interlocked.Exchange(ref reservedBytes, 0));
    }
}
