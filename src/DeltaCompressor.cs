using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipelines;
using System.Numerics;

namespace OatIM.DeltaCompression;

/// <summary>
/// Orchestrates the delta compression and decompression of an array of IDeltaSerializable objects.
/// </summary>
/// <typeparam name="T">The type of the state object to compress.</typeparam>
/// <typeparam name="TContext">The type of the context to use for the packet.</typeparam>
/// <threadsafety>
///   <static>
///     Any public static members of <see cref="DeltaCompressor{T,TContext}"/> are
///     thread-safe.
///   </static>
///   <instance>
///     Instance members are **not** guaranteed to be thread-safe.  If a single
///     compressor will be accessed from multiple threads, callers must provide
///     their own synchronization.
///   </instance>
/// </threadsafety>
public class DeltaCompressor<T, TContext>
    where T : struct, IDeltaSerializable<T, TContext>
    where TContext : struct, IDeltaContext
{
    private T[] _lastSentState;
    private T[] _currentState;

    /// <summary>
    /// The size of the header for each delta packet. (One uint32 for the length prefix)
    /// </summary>
    private const int HeaderSize = 4;

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
        if (stateArraySize < 1)
            throw new ArgumentOutOfRangeException(nameof(stateArraySize), "State array size must be at least 1.");
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
        if (initialState == null)
            throw new ArgumentNullException(nameof(initialState), "Initial state cannot be null.");
        if (initialState.Length != _currentState.Length)
            throw new ArgumentException($"Initial state must have the same length as the compressor's state array ({_currentState.Length}).", nameof(initialState));

        Array.Copy(initialState, _currentState, initialState.Length);
        Array.Copy(initialState, _lastSentState, initialState.Length);
    }

    /// <summary>
    /// Compares a new state array to the last sent state, writes a delta packet to the writer,
    /// and updates its internal cache to the new state.
    /// Note that if you are using this method alongside the `ApplyDeltaPacketAsync` method,
    /// you should ensure that you are properly synchronizing the state before writing deltas,
    /// `WriteDeltaPacketAsync` is not thread-safe.
    /// </summary>
    /// <param name="writer">The PipeWriter to write the delta packet to.</param>
    /// <param name="newState">The new state array to compare against.</param>
    /// <param name="context">The global context for this packet.</param>
    public async Task WriteDeltaPacketAsync(PipeWriter writer, T[] newState, TContext context)
    {
        if (newState == null)
            throw new ArgumentNullException(nameof(newState), "New state cannot be null.");
        if (newState.Length != _currentState.Length)
            throw new ArgumentException($"New state must have the same length as the compressor's state array ({_currentState.Length}).", nameof(newState));

        Array.Copy(newState, _currentState, newState.Length);

        // Reserve exactly 4 bytes for the length prefix.
        var prefixSpan = writer.GetSpan(HeaderSize);
        writer.Advance(HeaderSize);

        long startBytes = writer.UnflushedBytes;

        // Write the body directly to the PipeWriter.
        context.Write(ref writer);

        // Write the individual deltas.
        for (int i = 0; i < _currentState.Length; i++)
        {
            ulong changeMask = _currentState[i].GetChangeMask(_lastSentState[i], context);
            if (changeMask == 0)
                continue; // No changes, skip writing this entry.

            WriteVarInt(writer, (uint)i);
            WriteVarInt(writer, changeMask);
            _currentState[i].WriteDelta(ref writer, changeMask);
        }

        // Calculate the length of the packet body (excluding the header).
        long endBytes = writer.UnflushedBytes;
        uint bodyLength = (uint)(endBytes - startBytes);

        BinaryPrimitives.WriteUInt32LittleEndian(prefixSpan, bodyLength);

        // Swap the buffers to avoid O(n) copies.
        SwapBuffers();

        await writer.FlushAsync();
    }

    /// <summary>
    /// Swaps the _currentState and _lastSentState arrays to avoid O(n) copies.
    /// </summary>
    private void SwapBuffers() => (_lastSentState, _currentState) = (_currentState, _lastSentState);

    /// <summary>
    /// Reads a delta packet from the reader and applies all changes to its internal state array.
    /// Note that if you are using this method alongside the `WriteDeltaPacketAsync` method,
    /// you should ensure that you are properly synchronizing the state before writing deltas,
    /// `ApplyDeltaPacketAsync` is not thread-safe.
    /// </summary>
    /// <param name="reader">The PipeReader to read the delta packet from.</param>
    public async Task ApplyDeltaPacketAsync(PipeReader reader)
    {
        ReadResult result;
        do
        {
            result = await reader.ReadAsync();
            var buffer = result.Buffer;
            var sequenceReader = new SequenceReader<byte>(buffer);
            var consumed = buffer.Start;

            // parse as many complete packets as are currently in the buffer
            while (TryReadAndApplyPacket(ref sequenceReader, out var end))
            {
                consumed = end;
            }

            reader.AdvanceTo(consumed, sequenceReader.Position);

        } while (!result.IsCompleted);   // ← only exit here
    }

    private bool TryReadAndApplyPacket(ref SequenceReader<byte> reader, out SequencePosition endPosition)
    {
        var startConsumed = reader.Consumed;

        // Read the length prefix. .NET 9 doesn't have handy helpers for unsigned values
        //  so we're going to read the four bytes as a signed integer and then convert it.
        if (!reader.TryReadLittleEndian(out int rawPacketLength))
        {
            reader.Rewind(reader.Consumed - startConsumed);
            endPosition = default;
            return false; // Not enough data for length prefix.
        }
        uint packetLength = unchecked((uint)rawPacketLength);

        // Check if the full packet is available
        if (reader.Remaining < packetLength)
        {
            reader.Rewind(reader.Consumed - startConsumed);
            endPosition = default;
            return false; // Not enough data for the full packet.
        }

        var bodyStartConsumed = reader.Consumed;

        // Read the context
        TContext context = new TContext();
        if (reader.Remaining < TContext.Size)
        {
            reader.Rewind(reader.Consumed - startConsumed);
            endPosition = default;
            return false; // Not enough data for context.
        }
        context.Read(ref reader);

        // Read deltas until packetLength is consumed.
        while ((reader.Consumed - bodyStartConsumed) < packetLength)
        {
            if (!TryReadVarInt(ref reader, out ulong rawIndex)
             || !TryReadVarInt(ref reader, out ulong rawMask))
            {
                reader.Rewind(reader.Consumed - startConsumed);
                endPosition = default;
                return false; // Not enough data for index or mask.
            }

            int index = checked((int)rawIndex);
            if ((uint)index >= (uint)_currentState.Length)
            {
                throw new InvalidOperationException($"Index {index} is out of bounds for the current state array of length {_currentState.Length}.");
            }

            int payloadSize = T.GetDeltaSize(rawMask);
            if (reader.Remaining < payloadSize)
            {
                reader.Rewind(reader.Consumed - startConsumed);
                endPosition = default;
                return false; // Not enough data for the delta payload.
            }

            _currentState[index].ApplyDelta(ref reader, rawMask);
        }

        // Apply context to all entries.
        for (int i = 0; i < _currentState.Length; i++)
            _currentState[i].ApplyContext(context);

        endPosition = reader.Position;
        return true; // Successfully read and applied a packet.
    }

    /// <summary>
    /// This method allows us to advance the baseline state to the current state.
    /// It's useful for when you want to write deltas after applying a full packet.
    /// (relays, proxies, etc.)
    /// 
    /// Call this after you call `ApplyDeltaPacketAsync` if you plan to write deltas with the
    /// same compressor instance afterwards.
    /// </summary>
    public void AdvanceBaseline()
    {
        // Update _lastSentState to match _currentState for next delta calculation.
        Array.Copy(_currentState, _lastSentState, _currentState.Length);
    }

    /// <summary>
    /// This method allows us to use a 7-bits of data 1-bit MSB of continuation.
    /// So the ceil of 64 / 7 is ~9.1 and therefore we would need 10 bytes to encode a 64-bit VarInt.
    /// </summary>
    /// <param name="writer">Stream we are writing to.</param>
    /// <param name="value">The value to write as a VarInt.</param>
    private void WriteVarInt(PipeWriter writer, ulong value)
    {
        Span<byte> tmp = stackalloc byte[10];
        int len = 0;
        ulong v = value;
        while (len < tmp.Length)
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            if (v > 0) b |= 0x80;
            tmp[len++] = b;
            if (v == 0) break; // No more bits to write.
        }
        tmp.Slice(0, len).CopyTo(writer.GetSpan(len));
        writer.Advance(len);
    }

    /// <summary>
    /// Writes a VarInt-encoded unsigned 32-bit integer.
    /// </summary>
    private void WriteVarInt(PipeWriter writer, uint value) => WriteVarInt(writer, (ulong)value);

    private bool TryReadVarInt(ref SequenceReader<byte> reader, out ulong value)
    {
        value = 0; int shift = 0; byte b;
        do
        {
            if (!reader.TryRead(out b)) return false;
            if (shift >= 64) throw new OverflowException("VarInt is too large.");
            value |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return true;
    }
}