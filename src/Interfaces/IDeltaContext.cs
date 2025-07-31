using System.Buffers;
using System.IO.Pipelines;

namespace OatIM.DeltaCompression;

/// <summary>
/// Provides a contract for a context object that contains global data for a delta compression packet.
/// This context is serialized once at the beginning of a packet.
/// </summary>
public interface IDeltaContext
{
    /// <summary>
    /// Gets the exact size of the context in bytes when it is serialized.
    /// This is crucial for the reader to validate that a packet is complete enough to be parsed.
    /// </summary>
    static abstract int Size { get; }

    /// <summary>
    /// Writes the context's data to the provided PipeWriter.
    /// </summary>
    /// <param name="writer">The writer to serialize the context to.</param>
    void Write(ref PipeWriter writer);

    /// <summary>
    /// Reads and populates the context's data from the provided SequenceReader.
    /// </summary>
    /// <param name="reader">The reader to deserialize the context from.</param>
    void Read(ref SequenceReader<byte> reader);
}