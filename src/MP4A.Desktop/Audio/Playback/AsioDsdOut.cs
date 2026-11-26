using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.Asio;

namespace Soundroom;

internal sealed class AsioDsdOut(string name, IntPtr window) : IWavePlayer
{
    private SynchronizationContext? callbackContext;
    private AsioDriverExt? driver;
    private IWaveProvider? provider;
    private byte[] packets = [], scratch = [];
    private AsioSampleType[] formats = [];
    private int frames, draining, stopScheduled;
    private volatile PlaybackState state;
    private bool disposed;
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public PlaybackState PlaybackState => state;
    public WaveFormat OutputWaveFormat => provider?.WaveFormat ?? throw new InvalidOperationException("The driver is not initialized.");
    internal double? DriverSampleRate()
    {
        double? rate = driver?.Driver.GetSampleRate();
        return rate is > 0 && double.IsFinite(rate.Value) ? rate : null;
    }
    public float Volume
    {
        get => 1;
        set { if (value != 1) throw new NotSupportedException("Native DSD uses fixed digital gain."); }
    }

    public unsafe void Init(IWaveProvider waveProvider)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        callbackContext = SynchronizationContext.Current;
        var device = AsioDriver.GetAsioDriverByName(name);
        try
        {
            if (!device.Init(window))
                throw new IOException("The ASIO driver could not initialize.");
            Span<byte> format = stackalloc byte[512];
            format.Clear();
            format[0] = 1; // kASIODSDFormat
            fixed (byte* data = format)
            {
                device.Future(0x23112004, (IntPtr)data); // kAsioCanDoIoFormat
                if (format[0] != 1)
                    throw new NotSupportedException("The ASIO driver did not accept native DSD.");
                device.Future(0x23111961, (IntPtr)data); // kAsioSetIoFormat
            }
            int bitClock = checked(waveProvider.WaveFormat.SampleRate * 16);
            if (!device.CanSampleRate(bitClock))
                throw new NotSupportedException($"The ASIO driver cannot use a DSD clock of {bitClock:N0} Hz.");
            device.SetSampleRate(bitClock);
            if (Math.Abs(device.GetSampleRate() - bitClock) > 1)
                throw new NotSupportedException("The ASIO driver reported a different clock rate.");
            driver = new AsioDriverExt(device);
            int channels = waveProvider.WaveFormat.Channels;
            if (driver.Capabilities.NbOutputChannels < channels)
                throw new NotSupportedException($"The ASIO driver does not expose {channels} output channels.");
            formats = driver.Capabilities.OutputChannelInfos.Take(channels).Select(info => info.type).ToArray();
            if (formats.Any(type => type is not (AsioSampleType.DSDInt8LSB1 or AsioSampleType.DSDInt8MSB1)))
                throw new NotSupportedException("The ASIO driver did not expose packed one-bit DSD channels.");
            int bufferSamples = driver.CreateBuffers(channels, 0, false);
            if (bufferSamples <= 0 || bufferSamples % 16 != 0 || bufferSamples > 8 * 1024 * 1024)
                throw new NotSupportedException($"Unsupported ASIO DSD buffer size ({bufferSamples}).");
            frames = bufferSamples / 16;
            packets = new byte[frames * channels * 3];
            scratch = new byte[frames * 2];
            provider = waveProvider;
            driver.FillBufferCallback = Fill;
        }
        catch
        {
            if (driver is null)
                device.ReleaseComAsioDriver();
            else
                driver.ReleaseDriver();
            driver = null;
            throw;
        }
    }

    private void Fill(IntPtr[] inputs, IntPtr[] outputs)
    {
        if (provider is null || outputs.Length < formats.Length)
            return;
        Exception? error = null;
        int count = 0;
        try { if (state == PlaybackState.Playing && draining == 0) count = provider.Read(packets, 0, packets.Length); }
        catch (Exception failure) { error = failure; }
        int valid = count / (3 * formats.Length);
        for (int channel = 0; channel < formats.Length; channel++)
        {
            bool lsb = formats[channel] == AsioSampleType.DSDInt8LSB1;
            scratch.AsSpan().Fill(lsb ? (byte)0x96 : (byte)0x69);
            for (int frame = 0; frame < valid; frame++)
            {
                int start = (frame * formats.Length + channel) * 3;
                scratch[frame * 2] = lsb ? DsdReader.Reverse(packets[start + 1]) : packets[start + 1];
                scratch[frame * 2 + 1] = lsb ? DsdReader.Reverse(packets[start]) : packets[start];
            }
            Marshal.Copy(scratch, 0, outputs[channel], scratch.Length);
        }
        if (count < packets.Length && state == PlaybackState.Playing && (++draining >= 3 || error is not null) &&
            Interlocked.Exchange(ref stopScheduled, 1) == 0)
        {
            void Finish(object? _)
            {
                if (disposed)
                    return;
                try { StopDriver(); }
                catch (Exception stopError) { error ??= stopError; }
                PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
            }
            if (callbackContext is not null)
                callbackContext.Post(Finish, null);
            else
                ThreadPool.QueueUserWorkItem(Finish);
        }
    }

    public void Play()
    {
        if (driver is null || state == PlaybackState.Playing)
            return;
        state = PlaybackState.Playing;
        try { driver.Start(); }
        catch { state = PlaybackState.Stopped; throw; }
    }

    public void Pause()
    {
        if (state != PlaybackState.Playing)
            return;
        driver?.Stop();
        state = PlaybackState.Paused;
    }

    private void StopDriver()
    {
        if (state == PlaybackState.Playing)
            driver?.Stop();
        state = PlaybackState.Stopped;
    }

    public void Stop() => StopDriver();

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try { StopDriver(); }
        finally { driver?.ReleaseDriver(); driver = null; }
    }
}
