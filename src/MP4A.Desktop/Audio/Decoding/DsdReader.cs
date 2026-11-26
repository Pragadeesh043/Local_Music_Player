using System.Buffers.Binary;
using System.Text;

namespace Soundroom;

internal sealed class DsdReader : IDecodedSource
{
    private readonly FileStream stream;
    private readonly int channels, outputBytes, block;
    private readonly bool planar, reverse;
    private readonly long start, payloadSize, samples;
    private readonly long endFrame;
    private readonly byte[] buffer;
    private long frame, cached = -1;
    private bool canceled;

    public static int CarrierRate(Track track)
    {
        if (track.Bits != 1 || !track.Codec.StartsWith("dsd_", StringComparison.Ordinal))
            throw new NotSupportedException("DoP requires a DSD stream in a DSF or uncompressed DSDIFF file.");
        // FFmpeg expresses DSD sample rate in groups of eight one-bit samples.
        return track.Rate / 2;
    }

    public DsdReader(Track track, double offset, int bits)
    {
        channels = track.Channels;
        outputBytes = bits / 8;
        stream = File.OpenRead(track.Path);
        try
        {
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            string type = Id(reader);
            if (type == "DSD ")
            {
                planar = true;
                stream.Position = 28;
                Require(Id(reader) == "fmt ");
                long fmtSize = reader.ReadInt64();
                int version = reader.ReadInt32(), format = reader.ReadInt32();
                _ = reader.ReadInt32();
                int channelCount = reader.ReadInt32(), rate = reader.ReadInt32(), bitOrder = reader.ReadInt32();
                long sampleBits = reader.ReadInt64();
                block = reader.ReadInt32();
                Require(version == 1 && format == 0 && channelCount == channels && rate == CarrierRate(track) * 16 &&
                    bitOrder is 1 or 8 && block is >= 2 and <= 65536 && block % 2 == 0 && sampleBits >= 0);
                reverse = bitOrder == 1;
                samples = sampleBits / 16;
                stream.Position = 28 + fmtSize;
                Require(Id(reader) == "data");
                payloadSize = reader.ReadInt64() - 12;
                start = stream.Position;
                buffer = new byte[block * channels];
            }
            else
            {
                Require(type == "FRM8");
                _ = ReadBig(reader);
                Require(Id(reader) == "DSD ");
                long size = 0;
                while (stream.Position + 12 <= stream.Length)
                {
                    string chunk = Id(reader);
                    size = ReadBig(reader);
                    Require(size >= 0 && size <= stream.Length - stream.Position);
                    if (chunk == "DSD ")
                        break;
                    stream.Position += size + (size & 1);
                    size = 0;
                }
                Require(size > 0);
                start = stream.Position;
                payloadSize = size;
                samples = size / (channels * 2);
                block = 4096;
                buffer = new byte[block * channels];
            }
            Require(payloadSize >= 0 && payloadSize <= stream.Length - start);
            frame = Math.Clamp((long)((track.Start + offset) * CarrierRate(track)), 0, samples);
            endFrame = track.CueIndex > 0 ? Math.Clamp((long)((track.Start + track.Duration) * CarrierRate(track)),
                frame, samples) : samples;
        }
        catch { stream.Dispose(); throw; }
    }

    private static string Id(BinaryReader input) => new(input.ReadChars(4));
    private static long ReadBig(BinaryReader input) => BinaryPrimitives.ReadInt64BigEndian(input.ReadBytes(8));
    private static void Require(bool condition)
    {
        if (!condition)
            throw new IOException("Unsupported or damaged DSD container.");
    }

    public int Read(Span<byte> target)
    {
        int written = 0;
        while (!Volatile.Read(ref canceled) && frame < endFrame && written + channels * outputBytes <= target.Length)
        {
            long byteIndex = frame * 2;
            long group = byteIndex / block;
            int position = (int)(byteIndex % block);
            if (group != cached)
            {
                long at = group * block * channels;
                stream.Position = start + at;
                int length = (int)Math.Min(payloadSize - at, buffer.Length);
                Require(length > 0);
                stream.ReadExactly(buffer.AsSpan(0, length));
                cached = group;
            }
            byte marker = (frame & 1) == 0 ? (byte)0x05 : (byte)0xFA;
            for (int channel = 0; channel < channels; channel++)
            {
                int first = planar ? channel * block + position : position * channels + channel;
                int second = first + (planar ? 1 : channels);
                if (outputBytes == 4)
                    target[written++] = 0;
                target[written++] = reverse ? Reverse(buffer[second]) : buffer[second];
                target[written++] = reverse ? Reverse(buffer[first]) : buffer[first];
                target[written++] = marker;
            }
            frame++;
        }
        return written;
    }

    internal static byte Reverse(byte value)
    {
        int bits = (value & 0x55) << 1 | (value >> 1 & 0x55);
        bits = (bits & 0x33) << 2 | (bits >> 2 & 0x33);
        return (byte)(bits << 4 | bits >> 4);
    }

    public void Cancel() => Volatile.Write(ref canceled, true);
    public void Dispose() { Cancel(); stream.Dispose(); }
}
