using System.Buffers;
using System.IO.Pipelines;

namespace OatIM.DeltaCompression;

/// <summary>
/// Provides a contract for a struct that can be delta-compressed.
/// </summary>
/// <typeparam name="T">The type of the struct itself.</typeparam>
/// <typeparam name="TContext">The type of the context packet used for serialization.</typeparam>
public interface IDeltaSerializable<T, TContext>
    where T : struct, IDeltaSerializable<T, TContext>
    where TContext : struct, IDeltaContext
{
    /// <summary>
    /// Compares the current state to a previous state and generates a bitmask of changed fields.
    /// </summary>
    /// <param name="oldState">The state from the previous snapshot to compare against.</param>
    /// <param name="context">The global packet context, which may influence the change mask.</param>
    /// <returns>A ulong bitmask where each bit represents a changed field.</returns>
    ulong GetChangeMask(T oldState, TContext context);

    /// <summary>
    /// Writes only the fields indicated by the change mask to the writer.
    /// </summary>
    /// <param name="writer">The writer to serialize the delta to.</param>
    /// <param name="changeMask">The bitmask indicating which fields have changed and should be written.</param>
    void WriteDelta(ref PipeWriter writer, ulong changeMask);

    /// <summary>
    /// Reads from the reader and applies the changed fields indicated by the change mask to the current instance.
    /// </summary>
    /// <param name="reader">The reader to deserialize the delta from.</param>
    /// <param name="changeMask">The bitmask indicating which fields should be read and applied.</param>
    void ApplyDelta(ref SequenceReader<byte> reader, ulong changeMask);

    /// <summary>
    /// Applies the global, packet-wide context to this state object. This is called on every object,
    /// even those that had no other changes.
    /// </summary>
    /// <param name="context">The deserialized context from the start of the packet.</param>
    void ApplyContext(TContext context);

    /// <summary>
    /// Gets the exact size in bytes for a given delta change mask.
    /// This is crucial for the reader to validate packet completeness before attempting to parse.
    /// </summary>
    /// <param name="changeMask">The change mask to calculate the size for.</param>
    /// <returns>The exact size in bytes of the delta payload.</returns>
    static abstract int GetDeltaSize(ulong changeMask);
}