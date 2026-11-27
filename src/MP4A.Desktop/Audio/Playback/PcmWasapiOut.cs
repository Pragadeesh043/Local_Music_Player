using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundroom;

internal sealed class PcmWasapiOut(string deviceId, bool exclusive, int validBits, int pauseFadeMilliseconds) : IWavePlayer
{
    private const int PollMilliseconds = 5;
    private const long BufferDuration = 1_000_000; // 100 ms in WASAPI reference time.
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? worker;
    private Playback? source;
    private TaskCompletionSource? play, pause;
    private volatile PlaybackState state;
    private bool closing, finished;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public PlaybackState PlaybackState => state;
    public WaveFormat OutputWaveFormat => source!.WaveFormat;
    public float Volume
    {
        get => 1;
        set { if (value != 1) throw new NotSupportedException("PCM gain is controlled by the playback pipeline."); }
    }

    public void Init(IWaveProvider waveProvider)
    {
        source = (Playback)waveProvider;
        worker = new Thread(Render) { IsBackground = true, Name = "MP4A PCM output", Priority = ThreadPriority.Highest };
        worker.Start();
        initialized.Task.GetAwaiter().GetResult();
    }

    public Task PlayAsync()
    {
        lock (gate)
        {
            if (closing || finished)
                return Task.CompletedTask;
            if (state == PlaybackState.Playing)
            {
                Complete(ref pause);
                wake.Set();
                return Task.CompletedTask;
            }
            play ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            wake.Set();
            return play.Task;
        }
    }

    public Task PauseAsync()
    {
        lock (gate)
        {
            Complete(ref play);
            if (state != PlaybackState.Playing || closing || finished)
                return Task.CompletedTask;
            pause ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            wake.Set();
            return pause.Task;
        }
    }

    public void Play() => PlayAsync().GetAwaiter().GetResult();
    public void Pause() => PauseAsync().GetAwaiter().GetResult();
    public void Stop()
    {
        lock (gate)
        {
            closing = true;
            wake.Set();
        }
        if (worker != Thread.CurrentThread)
            worker?.Join();
    }

    private static void Complete(ref TaskCompletionSource? request)
    {
        var completion = request;
        request = null;
        completion?.TrySetResult();
    }

    private void Render()
    {
        Exception? failure = null;
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var endpoint = devices.GetDevice(deviceId);
            using var client = endpoint.AudioClient;
            var mode = exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
            if (exclusive && !client.IsFormatSupported(mode, OutputWaveFormat))
                throw new NotSupportedException("The requested exclusive format is no longer available.");
            // Timer-driven rendering exposes padding in both modes so the pause tail can drain.
            var flags = exclusive ? AudioClientStreamFlags.None :
                AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
            client.Initialize(mode, flags, BufferDuration, 0, OutputWaveFormat, Guid.Empty);
            var renderer = client.AudioRenderClient;
            int capacity = client.BufferSize;
            int alignment = OutputWaveFormat.BlockAlign;
            byte[] buffer = new byte[capacity * alignment];
            double startupStep = 1d / Math.Max(1, OutputWaveFormat.SampleRate * 6 / 1000 - 1);
            double pauseStep = 1d / Math.Max(1, OutputWaveFormat.SampleRate * pauseFadeMilliseconds / 1000 - 1);
            int tailFrames = Math.Clamp(OutputWaveFormat.SampleRate * 25 / 1000, 1, capacity);
            var quantizer = new Quantizer();
            double phase = 0, riseStep = startupStep;
            int silentTail = 0;
            bool started = false, ending = false;
            long drainStarted = 0;
            initialized.TrySetResult();

            try
            {
                while (true)
                {
                    int wait;
                    lock (gate)
                    {
                        if (closing)
                            break;
                        if (!started)
                        {
                            wait = play is null ? Timeout.Infinite : PollMilliseconds;
                            if (play is not null && source!.CanPrimePcm(capacity))
                            {
                                phase = 0;
                                riseStep = state == PlaybackState.Paused ? pauseStep : startupStep;
                                if (Fill(capacity, fadingOut: false) == 0)
                                    break;
                                client.Start();
                                started = true;
                                state = PlaybackState.Playing;
                                Complete(ref play);
                            }
                        }
                        else
                        {
                            wait = PollMilliseconds;
                            int padding = client.CurrentPadding;
                            int available = capacity - padding;
                            if (ending || (pause is not null && phase == 0))
                            {
                                // Once padding contains only silence, discarding it cannot skip media.
                                if (padding <= silentTail)
                                {
                                    client.Stop();
                                    started = false;
                                    client.Reset();
                                    if (ending)
                                        break;
                                    state = PlaybackState.Paused;
                                    silentTail = 0;
                                    drainStarted = 0;
                                    Complete(ref pause);
                                    if (play is not null)
                                        wait = 0;
                                }
                                else
                                {
                                    if (Stopwatch.GetElapsedTime(drainStarted) > TimeSpan.FromSeconds(2))
                                        throw new IOException("The audio device did not drain its PCM buffer.");
                                    int silence = Math.Min(available, tailFrames - silentTail);
                                    if (silence > 0)
                                    {
                                        renderer.GetBuffer(silence);
                                        renderer.ReleaseBuffer(silence, AudioClientBufferFlags.Silent);
                                        silentTail += silence;
                                    }
                                }
                            }
                            else if (available > 0)
                            {
                                // A canceled pause can still have silence queued ahead of the next packet.
                                silentTail = 0;
                                bool fadingOut = pause is not null;
                                int frames = fadingOut ? Math.Min(available, (int)Math.Ceiling(phase / pauseStep)) : available;
                                Fill(frames, fadingOut);
                            }
                        }
                    }
                    wake.WaitOne(wait);
                }
            }
            finally
            {
                if (started)
                    client.Stop();
            }

            int Fill(int frames, bool fadingOut)
            {
                int bytes = source!.Read(buffer, 0, frames * alignment);
                if (bytes > 0)
                {
                    int count = bytes / alignment;
                    if (fadingOut)
                        riseStep = pauseStep;
                    if (fadingOut || phase < 1)
                        Ramp(buffer.AsSpan(0, bytes), ref phase, fadingOut ? pauseStep : riseStep, fadingOut, quantizer);
                    IntPtr destination = renderer.GetBuffer(count);
                    Marshal.Copy(buffer, 0, destination, bytes);
                    renderer.ReleaseBuffer(count, AudioClientBufferFlags.None);
                    source.ConfirmPcmPacket();
                }
                ending = bytes < frames * alignment;
                if (ending || (fadingOut && phase == 0))
                    drainStarted = Stopwatch.GetTimestamp();
                return bytes;
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            bool notify;
            lock (gate)
            {
                finished = true;
                state = PlaybackState.Stopped;
                notify = !closing && initialized.Task.IsCompletedSuccessfully;
                if (failure is not null)
                    initialized.TrySetException(failure);
                Complete(ref play);
                Complete(ref pause);
            }
            if (notify)
                PlaybackStopped?.Invoke(this, new StoppedEventArgs(failure));
        }
    }

    private void Ramp(Span<byte> packet, ref double phase, double step, bool fadingOut, Quantizer quantizer)
    {
        int alignment = OutputWaveFormat.BlockAlign;
        int sampleBytes = OutputWaveFormat.BitsPerSample / 8;
        for (int offset = 0; offset < packet.Length; offset += alignment)
        {
            if (fadingOut)
                phase = phase <= step * 1.000001 ? 0 : phase - step;
            double gain = phase * phase * (3 - 2 * phase);
            if (!fadingOut)
                phase = Math.Min(1, phase + step);
            if (gain == 1)
                break;
            var samples = packet.Slice(offset, alignment);
            if (gain == 0)
            {
                samples.Clear();
                continue;
            }
            if (!exclusive)
            {
                foreach (ref float sample in MemoryMarshal.Cast<byte, float>(samples))
                    sample *= (float)gain;
            }
            else
            {
                for (int i = 0; i < samples.Length; i += sampleBytes)
                {
                    var sample = samples.Slice(i, sampleBytes);
                    quantizer.Write(sample, Quantizer.Decode(sample) * gain, validBits, DitherMode.Off, 0);
                }
            }
        }
    }

    public void Dispose()
    {
        Stop();
        wake.Dispose();
    }
}
