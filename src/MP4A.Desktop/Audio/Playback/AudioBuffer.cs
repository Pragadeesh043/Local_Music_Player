namespace Soundroom;

// One producer publishes complete blocks; one consumer owns the offset into the head block.
internal sealed class AudioBuffer(int capacity, int blockBytes)
{
    internal sealed class Block(int size)
    {
        public byte[] Data { get; } = new byte[size];
        public int Length;
        public Signal? Signal;
        public ProcessingSnapshot? Processing;
        public List<(Track Track, long Frame)> Transitions { get; } = [];
    }

    private readonly Block[] blocks = Enumerable.Range(0, capacity).Select(_ => new Block(blockBytes)).ToArray();
    private long readIndex, writeIndex;

    public Block? Writable()
    {
        long index = writeIndex;
        return index - Volatile.Read(ref readIndex) < capacity ? blocks[index % capacity] : null;
    }

    public int Queued => (int)(Volatile.Read(ref writeIndex) - Volatile.Read(ref readIndex));
    public bool BelowScanReserve => Queued < capacity / 2;
    public void Publish() => Volatile.Write(ref writeIndex, writeIndex + 1);

    public Block? Readable()
    {
        long index = readIndex;
        return index < Volatile.Read(ref writeIndex) ? blocks[index % capacity] : null;
    }

    public void Release() => Volatile.Write(ref readIndex, readIndex + 1);
}
