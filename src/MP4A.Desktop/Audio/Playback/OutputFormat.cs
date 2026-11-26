using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundroom;

internal sealed record DirectFormat(WaveFormat Format, int ValidBits, string Encoding)
{
    public static DirectFormat Match(AudioClient client, Track track, int rate, string[] positions)
    {
        int precision = track.Bits;
        bool integerSource = precision is >= 8 and <= 32 &&
            (track.Codec is "flac" or "alac" or "wavpack" or "ape" or "tak" || track.Codec.StartsWith("pcm_s", StringComparison.Ordinal) ||
                track.Codec.StartsWith("pcm_u", StringComparison.Ordinal));
        if (!integerSource)
            throw new NotSupportedException("Exclusive direct mode requires integer PCM or a supported lossless PCM codec.");
        int mask = ChannelMask(positions);
        foreach (int bits in new[] { 16, 24, 32 }.Where(bits => bits >= precision))
        {
            int valid = bits == 32 && precision is > 16 and <= 24 ? 24 : bits;
            var format = Extensible(rate, track.Channels, bits, valid, mask, floating: false);
            if (client.IsFormatSupported(AudioClientShareMode.Exclusive, format))
                return new(format, valid, $"s{bits}le");
            if (track.Channels <= 2 && valid == bits)
            {
                format = new WaveFormat(rate, bits, track.Channels);
                if (client.IsFormatSupported(AudioClientShareMode.Exclusive, format))
                    return new(format, valid, $"s{bits}le");
            }
            if (bits == 32 && valid != 32)
            {
                format = Extensible(rate, track.Channels, 32, 32, mask, floating: false);
                if (client.IsFormatSupported(AudioClientShareMode.Exclusive, format))
                    return new(format, 32, "s32le");
            }
        }
        throw new NotSupportedException($"The device cannot accept {track.Channels} channels at {rate:N0} Hz with {precision}-bit precision in exclusive mode.");
    }

    public static int ChannelMask(string[] channels)
    {
        string[] windowsOrder = ["FL", "FR", "FC", "LFE", "BL", "BR", "FLC", "FRC", "BC", "SL", "SR"];
        int mask = 0, last = -1;
        foreach (string speaker in channels)
        {
            int bit = Array.IndexOf(windowsOrder, speaker);
            if (bit <= last)
                throw new NotSupportedException("The speaker layout has no exact WASAPI channel mapping.");
            mask |= 1 << bit;
            last = bit;
        }
        return mask;
    }

    public static unsafe WaveFormat Extensible(int rate, int channels, int bits, int validBits, int mask, bool floating)
    {
        byte[] bytes = new byte[40];
        using var writer = new BinaryWriter(new MemoryStream(bytes, writable: true));
        writer.Write((ushort)0xfffe);
        writer.Write((ushort)channels);
        writer.Write(rate);
        int alignment = channels * bits / 8;
        writer.Write(rate * alignment);
        writer.Write((ushort)alignment);
        writer.Write((ushort)bits);
        writer.Write((ushort)22);
        writer.Write((ushort)validBits);
        writer.Write(mask);
        writer.Write(new Guid(floating ? "00000003-0000-0010-8000-00aa00389b71" :
            "00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        fixed (byte* buffer = bytes)
            return WaveFormat.MarshalFromPtr((IntPtr)buffer);
    }
}
