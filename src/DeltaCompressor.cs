using System.Buffers;
using System.IO.Pipelines;

namespace Oat.IO.DeltaCompression;

/// <summary>
/// Orchestrates the delta compression and decompression of an array of IDeltaSerializable objects.
/// </summary>
/// <typeparam name="T">The type of the state object to compress.</typeparam>
/// <typeparam name="TContext">The type of the context to use for the packet.</typeparam>
public class DeltaCompressor<T, TContext>
    where T : struct, IDeltaSerializable<T, TContext>
    where TContext : struct, IDeltaContext
{
    private T[] _lastSentState;
    private T[] _currentState;

    /// <summary>
    /// Gets the current, fully synchronized state array.
    /// </summary>
    public IReadOnlyList<T> CurrentState => _currentState;

    /// <summary>
    /// Initializes a new DeltaCompressor for a state array of a fixed size.
    /// </summary>
    /// <param name="stateArraySize">The total number of state objects in the array (e.g., max players).</param>
    public DeltaCompressor(int stateArraySize)
    {
        _lastSentState = new T[stateArraySize];
        _currentState = new T[stateArraySize];
    }
    
    /// <summary>
    /// Sets the initial baseline state for the compressor. Should be called on both client and server
    /// with the same data (e.g., after a full keyframe synchronization).
    /// </summary>
    /// <param name="initialState">The initial state array.</param>
    public void SetInitialState(T[] initialState)
    {
        Array.Copy(initialState, _currentState, initialState.Length);
        Array.Copy(initialState, _lastSentState, initialState.Length);
    }

    /// <summary>
    /// Compares a new state array to the last sent state, writes a delta packet to the writer,
    /// and updates its internal cache to the new state.
    /// </summary>
    /// <param name="writer">The PipeWriter to write the delta packet to.</param>
    /// <param name="newState">The new state array to compare against.</param>
    /// <param name="context">The global context for this packet.</param>
    public async Task WriteDeltaPacketAsync(PipeWriter writer, T[] newState, TContext context)
    {
        Array.Copy(newState, _currentState, newState.Length);

        // Write the generic context to the stream.
        context.Write(ref writer);

        // Write the individual deltas.
        for (int i = 0; i < _currentState.Length; i++)
        {
            ulong changeMask = _currentState[i].GetChangeMask(_lastSentState[i], context);
            if (changeMask != 0)
            {
                WriteVarInt(writer, (uint)i);
                WriteVarInt(writer, changeMask);
                _currentState[i].WriteDelta(ref writer, changeMask);
            }
        }

        // Update the cache for the next run.
        Array.Copy(_currentState, _lastSentState, _currentState.Length);
        await writer.FlushAsync();
    }

    /// <summary>
    /// Reads a delta packet from the reader and applies all changes to its internal state array.
    /// </summary>
    /// <param name="reader">The PipeReader to read the delta packet from.</param>
    public async Task ApplyDeltaPacketAsync(PipeReader reader)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;
            var sequenceReader = new SequenceReader<byte>(buffer);

            if (TryReadAndApplyPacket(ref sequenceReader))
            {
                 reader.AdvanceTo(sequenceReader.Position, buffer.End);
            }
            else
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
            }

            if (result.IsCompleted) break;
        }
    }

    private bool TryReadAndApplyPacket(ref SequenceReader<byte> reader)
    {
        var initialReaderPosition = reader.Position;

        // Create and read the generic context.
        TContext context = new TContext();
        if (reader.Remaining < context.Size) return false; // Check for context completeness.
        context.Read(ref reader);

        // Read deltas until the buffer is exhausted.
        while (reader.Remaining > 0)
        {
            var deltaStartPosition = reader.Position;
            if (!TryReadVarInt(ref reader, out ulong index) || !TryReadVarInt(ref reader, out ulong changeMask))
            {
                reader.Rewind(reader.Consumed - deltaStartPosition.GetInteger());
                break; // No more full deltas in the buffer, exit loop.
            }

            int payloadSize = default(T).GetDeltaSize(changeMask);
            if (reader.Remaining < payloadSize)
            {
                reader.Rewind(reader.Consumed - deltaStartPosition.GetInteger());
                break; // Not enough data for this delta, exit loop.
            }

            _currentState[index].ApplyDelta(ref reader, changeMask);
        }
        
        // Apply the context to ALL entities after processing the deltas.
        for (int i = 0; i < _currentState.Length; i++)
        {
            _currentState[i].ApplyContext(context);
        }

        return true;
    }

    private void WriteVarInt(PipeWriter writer, ulong value)
    {
        do {
            var buffer = writer.GetSpan(1);
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value > 0) b |= 0x80;
            buffer[0] = b;
            writer.Advance(1);
        } while (value > 0);
    }

    private bool TryReadVarInt(ref SequenceReader<byte> reader, out ulong value)
    {
        value = 0; int shift = 0; byte b;
        do {
            if (!reader.TryRead(out b)) return false;
            value |= (ulong)(b & 0x7F) << shift;
            if (shift > 63) throw new OverflowException("VarInt is too large.");
            shift += 7;
        } while ((b & 0x80) != 0);
        return true;
    }
}