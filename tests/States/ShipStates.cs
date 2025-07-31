using OatIM.DeltaCompression.Tests.Contexts;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace OatIM.DeltaCompression.Tests.States;

/// <summary>
/// A test implementation of a delta-serializable game state object.
/// </summary>
public struct ShipState : IDeltaSerializable<ShipState, GlobalTickContext>
{
    [Flags]
    private enum ChangeMask : ulong
    {
        None      = 0,
        PosX      = 1 << 0,
        PosY      = 1 << 1,
        Yaw       = 1 << 2,
        Vel       = 1 << 3,
        InputMask = 1 << 4,
    }

    public int PosX { get; set; }
    public int PosY { get; set; }
    public ushort Yaw { get; set; }
    public ushort Vel { get; set; }
    public ulong Tick { get; set; }
    public uint InputMask { get; set; }

    public ulong GetChangeMask(ShipState oldState, GlobalTickContext context)
    {
        ChangeMask mask = ChangeMask.None;
        if (PosX != oldState.PosX) mask |= ChangeMask.PosX;
        if (PosY != oldState.PosY) mask |= ChangeMask.PosY;
        if (Yaw != oldState.Yaw) mask |= ChangeMask.Yaw;
        if (Vel != oldState.Vel) mask |= ChangeMask.Vel;
        if (InputMask != oldState.InputMask) mask |= ChangeMask.InputMask;
        return (ulong)mask;
    }

    public void WriteDelta(ref PipeWriter writer, ulong changeMask)
    {
        var mask = (ChangeMask)changeMask;
        if (mask.HasFlag(ChangeMask.PosX)) WriteInt(ref writer, PosX);
        if (mask.HasFlag(ChangeMask.PosY)) WriteInt(ref writer, PosY);
        if (mask.HasFlag(ChangeMask.Yaw)) WriteUShort(ref writer, Yaw);
        if (mask.HasFlag(ChangeMask.Vel)) WriteUShort(ref writer, Vel);
        if (mask.HasFlag(ChangeMask.InputMask)) WriteUInt(ref writer, InputMask);
    }

    public void ApplyDelta(ref SequenceReader<byte> reader, ulong changeMask)
    {
        var mask = (ChangeMask)changeMask;
        if (mask.HasFlag(ChangeMask.PosX)) { reader.TryReadLittleEndian(out int val); PosX = val; }
        if (mask.HasFlag(ChangeMask.PosY)) { reader.TryReadLittleEndian(out int val); PosY = val; }
        if (mask.HasFlag(ChangeMask.Yaw)) { reader.TryReadLittleEndian(out short val); Yaw = (ushort)val; }
        if (mask.HasFlag(ChangeMask.Vel)) { reader.TryReadLittleEndian(out short val); Vel = (ushort)val; }
        if (mask.HasFlag(ChangeMask.InputMask)) { reader.TryReadLittleEndian(out int val); InputMask = (uint)val; }
    }
    
    public void ApplyContext(GlobalTickContext context)
    {
        this.Tick = context.GlobalTick;
    }

    public int GetDeltaSize(ulong changeMask)
    {
        var mask = (ChangeMask)changeMask;
        int size = 0;
        if (mask.HasFlag(ChangeMask.PosX)) size += sizeof(int);
        if (mask.HasFlag(ChangeMask.PosY)) size += sizeof(int);
        if (mask.HasFlag(ChangeMask.Yaw)) size += sizeof(ushort);
        if (mask.HasFlag(ChangeMask.Vel)) size += sizeof(ushort);
        if (mask.HasFlag(ChangeMask.InputMask)) size += sizeof(uint);
        return size;
    }
    
    private void WriteInt(ref PipeWriter w, int val) { var s = w.GetSpan(4); BinaryPrimitives.WriteInt32LittleEndian(s, val); w.Advance(4); }
    private void WriteUShort(ref PipeWriter w, ushort val) { var s = w.GetSpan(2); BinaryPrimitives.WriteUInt16LittleEndian(s, val); w.Advance(2); }
    private void WriteUInt(ref PipeWriter w, uint val) { var s = w.GetSpan(4); BinaryPrimitives.WriteUInt32LittleEndian(s, val); w.Advance(4); }
    
    public override string ToString() => $"Tick:{Tick}, Pos:({PosX},{PosY}), Yaw:{Yaw}, Vel:{Vel}, Inputs:{InputMask:X}";
}
