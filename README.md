# OatIM.DeltaCompression · **v1.1.01**
<sub>Fast, allocation-free delta compression for .NET 8+</sub>

---

## 0  TL;DR  

```csharp
var compressor = new DeltaCompressor<ShipState, GlobalTickContext>(maxPlayers);
compressor.SetInitialState(snapshot0);          // key-frame
await compressor.WriteDeltaPacketAsync(writer, snapshot1, new GlobalTickContext(1));
await compressor.ApplyDeltaPacketAsync(reader); // on the receiving side
````

---

## 1  What’s new in 1.1.x

| Area            | Highlights                                                                                                                                                                  |
| --------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Reliability** | 100 % line **and** branch coverage. <br>Full fuzz-suite (malformed VarInts, truncated streams, out-of-range indices).                                                       |
| **Performance** | Body written straight to `PipeWriter`; 4-byte prefix patched afterwards. <br>`SwapBuffers()` removes sender-side O(*n*) copy.                                               |
| **API**         | Static-interface members:<br>• `IDeltaContext.Size`<br>• `IDeltaSerializable.GetDeltaSize`  → compile-time constants. <br>New `AdvanceBaseline()` helper for relay servers. |
| **Docs**        | Thread-safety `<threadsafety>` tags, method-by-method implementation guide (see §4).                                                                                        |

---

## 2  Why delta compression?

Sending the whole snapshot every tick is wasteful.
Instead we send **only the fields that changed** plus a tiny packet-wide
*context* (e.g. the current tick).
Typical savings for fast-moving game objects: **10×–100×** smaller packets.

---

## 3  Installing

```bash
dotnet add package OatIM.DeltaCompression --version 1.1.1
```

Target framework(s): **net8.0**, **net9.0**.

---

## 4  Implementation guide (method-by-method)

### 4.1  Create your packet context   `IDeltaContext`

| Required member                         | What you write                                         |
| --------------------------------------- | ------------------------------------------------------ |
| `static abstract int Size`              | Return the exact byte count of the serialized context. |
| `void Write(ref PipeWriter w)`          | Write **exactly** `Size` bytes (little-endian).        |
| `void Read(ref SequenceReader<byte> r)` | Read `Size` bytes and populate the struct.             |

```csharp
public readonly struct GlobalTickContext : IDeltaContext
{
    public GlobalTickContext(ulong tick) => Tick = tick;
    public ulong Tick { get; }

    public static int Size => sizeof(ulong);

    public void Write(ref PipeWriter w)
    {
        var span = w.GetSpan(Size);
        BinaryPrimitives.WriteUInt64LittleEndian(span, Tick);
        w.Advance(Size);
    }

    public void Read(ref SequenceReader<byte> r)
    {
        r.TryReadLittleEndian(out ulong t);
        this = new GlobalTickContext(t);
    }
}
```

### 4.2  Create your state struct   `IDeltaSerializable<T,TContext>`

Implement **five** methods:

| Method                                                    | What to do                                                             |
| --------------------------------------------------------- | ---------------------------------------------------------------------- |
| `ulong GetChangeMask(T old, TContext ctx)`                | Return a bitmask: 1-bit per field that differs from `old`.             |
| `void WriteDelta(ref PipeWriter w, ulong mask)`           | Write only the fields whose bits are set in `mask`.                    |
| `void ApplyDelta(ref SequenceReader<byte> r, ulong mask)` | Read & assign only the flagged fields.                                 |
| `void ApplyContext(TContext ctx)`                         | Apply packet-wide context (e.g. copy the tick).                        |
| `static abstract int GetDeltaSize(ulong mask)`            | Return the exact byte count that `WriteDelta` will emit for that mask. |

Example:

```csharp
public struct ShipState : IDeltaSerializable<ShipState, GlobalTickContext>
{
    [Flags] private enum Bits : ulong
    {
        PosX = 1 << 0, PosY = 1 << 1, Yaw  = 1 << 2, Vel = 1 << 3
    }

    public int PosX, PosY;
    public ushort Yaw, Vel;
    public ulong Tick;

    /* 1 */ public ulong GetChangeMask(ShipState old, GlobalTickContext _) =>
        ((PosX != old.PosX) ? Bits.PosX : 0) |
        ((PosY != old.PosY) ? Bits.PosY : 0) |
        ((Yaw  != old.Yaw ) ? Bits.Yaw  : 0) |
        ((Vel  != old.Vel ) ? Bits.Vel  : 0);

    /* 2 */ public void WriteDelta(ref PipeWriter w, ulong m)
    {
        if ((m & (ulong)Bits.PosX) != 0) w.WriteIntLE(PosX);
        if ((m & (ulong)Bits.PosY) != 0) w.WriteIntLE(PosY);
        if ((m & (ulong)Bits.Yaw ) != 0) w.WriteUShortLE(Yaw);
        if ((m & (ulong)Bits.Vel ) != 0) w.WriteUShortLE(Vel);
    }

    /* 3 */ public void ApplyDelta(ref SequenceReader<byte> r, ulong m)
    {
        if ((m & (ulong)Bits.PosX) != 0) r.TryReadLittleEndian(out PosX);
        if ((m & (ulong)Bits.PosY) != 0) r.TryReadLittleEndian(out PosY);
        if ((m & (ulong)Bits.Yaw ) != 0) r.TryReadLittleEndian(out ushort yaw); Vel = yaw;
        if ((m & (ulong)Bits.Vel ) != 0) r.TryReadLittleEndian(out ushort vel); Yaw = vel;
    }

    /* 4 */ public void ApplyContext(GlobalTickContext ctx) => Tick = ctx.Tick;

    /* 5 */ public static int GetDeltaSize(ulong m) =>
        ((m & (ulong)Bits.PosX) != 0 ? 4 : 0) +
        ((m & (ulong)Bits.PosY) != 0 ? 4 : 0) +
        ((m & (ulong)Bits.Yaw ) != 0 ? 2 : 0) +
        ((m & (ulong)Bits.Vel ) != 0 ? 2 : 0);
}
```

Helper extensions for brevity:

```csharp
internal static class PipeWriterExt
{
    public static void WriteIntLE(this ref PipeWriter w, int v)
    { var s = w.GetSpan(4); BinaryPrimitives.WriteInt32LittleEndian(s, v); w.Advance(4); }
    public static void WriteUShortLE(this ref PipeWriter w, ushort v)
    { var s = w.GetSpan(2); BinaryPrimitives.WriteUInt16LittleEndian(s, v); w.Advance(2); }
}
```

---

## 5  Using `DeltaCompressor`

```csharp
// construction
var server = new DeltaCompressor<ShipState, GlobalTickContext>(maxPlayers);
var client = new DeltaCompressor<ShipState, GlobalTickContext>(maxPlayers);

// baseline sync (key-frame)
server.SetInitialState(initial); client.SetInitialState(initial);

// each tick on the server
await server.WriteDeltaPacketAsync(pipe.Writer, newSnapshot,
                                   new GlobalTickContext(tick));

// each tick on the client
await client.ApplyDeltaPacketAsync(pipe.Reader);
```

### 5.1 Relay / proxy

After the client decodes a packet **and plans to re-encode it**:

```csharp
client.AdvanceBaseline();   // move last-sent-state → current-state
```

---

## 6  Thread-safety

```xml
<threadsafety>
  <static>All public static members are thread-safe.</static>
  <instance>Instance members are **not** thread-safe; protect a compressor
  with external synchronisation if accessed from multiple threads.</instance>
</threadsafety>
```

---

## 7  Building, testing & coverage

```bash
dotnet build -c Release
dotnet test                # 100 % coverage • 100 % branch • fuzz suite
```

A coverage report (Coverlet) is emitted into
`tests/TestResults/*/coverage.cobertura.xml`.

---

## 9  License

MIT — © Oat Interactive Media 2025.
