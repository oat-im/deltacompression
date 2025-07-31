using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Oat.IO.DeltaCompression.Tests.Contexts;

/// <summary>
/// A test implementation of a delta context object.
/// </summary>
public struct GlobalTickContext : IDeltaContext
{
    public ulong GlobalTick { get; set; }

    public int Size => sizeof(ulong);

    public void Write(ref PipeWriter writer)
    {
        var buffer = writer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, GlobalTick);
        writer.Advance(sizeof(ulong));
    }

    public void Read(ref SequenceReader<byte> reader)
    {
        reader.TryReadLittleEndian(out long tick);
        GlobalTick = (ulong)tick;
    }
}
