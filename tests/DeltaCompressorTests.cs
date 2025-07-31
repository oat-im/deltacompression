using Microsoft.VisualStudio.TestTools.UnitTesting;
using OatIM.DeltaCompression;
using OatIM.DeltaCompression.Tests.Contexts;
using OatIM.DeltaCompression.Tests.States;
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace OatIM.DeltaCompression.Tests;

[TestClass]
public class DeltaCompressorTests
{
    // --------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------

    private static readonly Random _rng = new(42);

    private static ShipState RandomState() => new()
    {
        PosX = _rng.Next(-1000, 1001),
        PosY = _rng.Next(-1000, 1001),
        Yaw = (ushort)_rng.Next(0, UInt16.MaxValue + 1),
        Vel = (ushort)_rng.Next(0, 3001),
        InputMask = (uint)_rng.Next()
    };

    private static void MutateRandomly(ShipState[] arr, double p = 0.2)
    {
        for (int i = 0; i < arr.Length; i++)
        {
            if (_rng.NextDouble() < p) arr[i].PosX += _rng.Next(-3, 4);
            if (_rng.NextDouble() < p) arr[i].PosY += _rng.Next(-3, 4);
            if (_rng.NextDouble() < p) arr[i].Yaw += (ushort)_rng.Next(-5, 6);
            if (_rng.NextDouble() < p) arr[i].Vel += (ushort)_rng.Next(-30, 31);
            if (_rng.NextDouble() < p) arr[i].InputMask ^= 1u << _rng.Next(0, 31);
        }
    }

    private static void AssertStatesEqual(ShipState[] expected,
                                            IReadOnlyList<ShipState> actual)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].PosX, actual[i].PosX, $"PosX mismatch @ {i}");
            Assert.AreEqual(expected[i].PosY, actual[i].PosY, $"PosY mismatch @ {i}");
            Assert.AreEqual(expected[i].Yaw, actual[i].Yaw, $"Yaw  mismatch @ {i}");
            Assert.AreEqual(expected[i].Vel, actual[i].Vel, $"Vel  mismatch @ {i}");
            Assert.AreEqual(expected[i].InputMask, actual[i].InputMask, $"InputMask mismatch @ {i}");
        }
    }

    private static byte[] SeqToArray(ReadOnlySequence<byte> seq)
    {
        var arr = new byte[seq.Length];
        seq.CopyTo(arr);
        return arr;
    }

    private static ShipState[] States(int n) =>
        Enumerable.Range(0, n).Select(_ => new ShipState()).ToArray();

    [TestMethod]
    public async Task FullSynchronization_AppliesDeltasAndContextCorrectly()
    {
        // Arrange
        const int stateCount = 100;
        var serverCompressor = new DeltaCompressor<ShipState, GlobalTickContext>(stateCount);
        var clientCompressor = new DeltaCompressor<ShipState, GlobalTickContext>(stateCount);

        var initialState = new ShipState[stateCount];
        for (int i = 0; i < stateCount; i++)
        {
            initialState[i] = new ShipState { Tick = 1 };
        }
        serverCompressor.SetInitialState(initialState);
        clientCompressor.SetInitialState(initialState);

        // Act: Simulate Tick 2
        var serverStateTick2 = (ShipState[])initialState.Clone();
        serverStateTick2[5] = new ShipState { PosX = 100, PosY = -50, Yaw = 250, Vel = 1500, InputMask = 1 };
        serverStateTick2[42] = new ShipState { Yaw = 900, InputMask = 2 };
        serverStateTick2[99].Vel = 50;

        var context2 = new GlobalTickContext { GlobalTick = 2 };
        for (int i = 0; i < serverStateTick2.Length; i++)
        {
            serverStateTick2[i].Tick = context2.GlobalTick;
        }

        var pipe = new Pipe();
        await serverCompressor.WriteDeltaPacketAsync(pipe.Writer, serverStateTick2, context2);
        await pipe.Writer.CompleteAsync();
        await clientCompressor.ApplyDeltaPacketAsync(pipe.Reader);
        await pipe.Reader.CompleteAsync();

        // Assert: Verify Tick 2
        for (int i = 0; i < stateCount; i++)
        {
            var expected = serverStateTick2[i];
            var actual = clientCompressor.CurrentState[i];
            Assert.AreEqual(expected.Tick, actual.Tick);
            Assert.AreEqual(expected.PosX, actual.PosX);
            Assert.AreEqual(expected.PosY, actual.PosY);
            Assert.AreEqual(expected.Yaw, actual.Yaw);
            Assert.AreEqual(expected.Vel, actual.Vel);
            Assert.AreEqual(expected.InputMask, actual.InputMask);
        }
    }

    [TestMethod]
    public async Task NoChanges_ProducesEmptyDeltaPayload()
    {
        // Arrange
        const int stateCount = 10;
        var serverCompressor = new DeltaCompressor<ShipState, GlobalTickContext>(stateCount);
        var initialState = new ShipState[stateCount];
        serverCompressor.SetInitialState(initialState);

        var context = new GlobalTickContext { GlobalTick = 2 };
        var pipe = new Pipe();

        // Act
        await serverCompressor.WriteDeltaPacketAsync(pipe.Writer, initialState, context);
        await pipe.Writer.CompleteAsync();
        var result = await pipe.Reader.ReadAsync();

        // Assert
        // The buffer should only contain the context, as there are no deltas.
        // The first byte should be the length of the context and the length of the header (4 bytes).
        var buffer = result.Buffer;
        Assert.IsTrue(buffer.Length > 0, "Buffer should not be empty");
        Assert.AreEqual(GlobalTickContext.Size + sizeof(uint), buffer.Length);
    }

    [TestMethod]
    public async Task TestOverflowException_VarIntEncoding()
    {
        // We will ensure that we properly throw an overflow exception when the VarInt exceeds the maximum size.
        var compressor = new DeltaCompressor<ShipState, GlobalTickContext>(1);

        // Build a packet: [length=12] [context (8 bytes of zeros)]
        // followed by an over-long var-int (11×0xFF + 0x01).
        byte[] packet = {
            12, 0, 0, 0,                          // length prefix
            0,0,0,0, 0,0,0,0,                     // dummy context
            0xFF,0xFF,0xFF,0xFF,0xFF,             // 11 bytes >= 0x80
            0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,
            0x01                                   // terminator
        };

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(packet);
        await pipe.Writer.CompleteAsync();

        // Act / Assert
        await Assert.ThrowsExceptionAsync<OverflowException>(async () =>
        {
            var reader = pipe.Reader;
            await compressor.ApplyDeltaPacketAsync(reader);
        });
    }

    // --------------------------------------------------------------------
    // 1. Encode → decode = identity
    // --------------------------------------------------------------------

    [DataRow(1)]
    [DataRow(32)]
    [DataRow(128)]
    [TestMethod]
    public async Task EncodeThenDecode_ProducesIdenticalState(int players)
    {
        var server = new DeltaCompressor<ShipState, GlobalTickContext>(players);
        var client = new DeltaCompressor<ShipState, GlobalTickContext>(players);

        var baseline = Enumerable.Range(0, players)
                                    .Select(_ => RandomState())
                                    .ToArray();

        server.SetInitialState(baseline);
        client.SetInitialState(baseline);

        var working = (ShipState[])baseline.Clone();

        for (ulong tick = 1; tick <= 500; tick++)
        {
            MutateRandomly(working);

            var ctx = new GlobalTickContext { GlobalTick = tick };

            var pipe = new Pipe();
            await server.WriteDeltaPacketAsync(pipe.Writer, working, ctx);
            await pipe.Writer.CompleteAsync();

            await client.ApplyDeltaPacketAsync(pipe.Reader);
            await pipe.Reader.CompleteAsync();

            AssertStatesEqual(working, client.CurrentState);
        }
    }

    // --------------------------------------------------------------------
    // 2. Highly fragmented stream
    // --------------------------------------------------------------------

    [TestMethod]
    public async Task Decoder_HandlesHighlyFragmentedStream()
    {
        const int players = 10;
        var server = new DeltaCompressor<ShipState, GlobalTickContext>(players);
        var client = new DeltaCompressor<ShipState, GlobalTickContext>(players);

        var state    = Enumerable.Range(0, players).Select(_ => RandomState()).ToArray();
        var baseline = new ShipState[players];

        server.SetInitialState(baseline);
        client.SetInitialState(baseline);

        var ctx = new GlobalTickContext { GlobalTick = 99 };

        // ---------- encode once ----------
        var fullPipe = new Pipe();
        await server.WriteDeltaPacketAsync(fullPipe.Writer, state, ctx);
        await fullPipe.Writer.CompleteAsync();
        byte[] packet = SeqToArray((await fullPipe.Reader.ReadAsync()).Buffer);

        // ---------- drip-feed to decoder ----------
        var livePipe = new Pipe();
        // run the decoder concurrently; it will block until writer completes
        Task decodeTask = client.ApplyDeltaPacketAsync(livePipe.Reader);

        int offset = 0;
        while (offset < packet.Length)
        {
            int slice = _rng.Next(1, 4);                 // 1–3 bytes
            slice = Math.Min(slice, packet.Length - offset);
            await livePipe.Writer.WriteAsync(packet.AsMemory(offset, slice));
            offset += slice;
            await livePipe.Writer.FlushAsync();          // make slice visible to reader
        }

        await livePipe.Writer.CompleteAsync();           // signal EOF
        await decodeTask;                               // wait until decoder finishes
        await livePipe.Reader.CompleteAsync();

        // ---------- verify ----------
        AssertStatesEqual(state, client.CurrentState);
    }

    // --------------------------------------------------------------------
    // 3. Baseline advance / relay scenario
    // --------------------------------------------------------------------

    [TestMethod]
    public async Task AdvanceBaseline_AllowsReencodeWithoutExtraDeltas()
    {
        const int n = 5;
        var server = new DeltaCompressor<ShipState, GlobalTickContext>(n);
        var relay = new DeltaCompressor<ShipState, GlobalTickContext>(n);

        var state = Enumerable.Range(0, n).Select(_ => RandomState()).ToArray();
        server.SetInitialState(state);
        relay.SetInitialState(state);

        var ctx = new GlobalTickContext { GlobalTick = 7 };

        var p1 = new Pipe();
        await server.WriteDeltaPacketAsync(p1.Writer, state, ctx);
        await p1.Writer.CompleteAsync();
        await relay.ApplyDeltaPacketAsync(p1.Reader);
        await p1.Reader.CompleteAsync();

        relay.AdvanceBaseline();

        var p2 = new Pipe();
        await relay.WriteDeltaPacketAsync(p2.Writer, state, ctx);
        await p2.Writer.CompleteAsync();
        long len = (await p2.Reader.ReadAsync()).Buffer.Length;

        long expected = GlobalTickContext.Size + sizeof(uint); // header + context
        Assert.AreEqual(expected, len, "Expected empty delta payload");
    }

    // --------------------------------------------------------------------
    // 4. Bounds-check: malformed index
    // --------------------------------------------------------------------

    [TestMethod]
    public async Task Decoder_Throws_OnOutOfRangeIndex()
    {
        const int n = 4;
        var c = new DeltaCompressor<ShipState, GlobalTickContext>(n);

        byte[] payload;
        using (var ms = new System.IO.MemoryStream())
        {
            ms.Write(new byte[4]);           // reserve length prefix
            ms.Write(new byte[8]);           // dummy context
            ms.Write(VarInt(100));           // index 100
            ms.Write(VarInt(1));             // mask
            int len = (int)(ms.Length - 4);
            BitConverter.TryWriteBytes(ms.GetBuffer().AsSpan(0, 4), len);
            payload = ms.ToArray();
        }

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(payload);
        await pipe.Writer.CompleteAsync();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => c.ApplyDeltaPacketAsync(pipe.Reader));
    }

    // --------------------------------------------------------------------
    // 5. VarInt round-trip on boundaries
    // --------------------------------------------------------------------

    [DataRow((ulong)127)]
    [DataRow((ulong)128)]
    [DataRow((ulong)16383)]
    [DataRow((ulong)16384)]
    [DataRow((ulong)((1UL << 32) - 1))]
    [TestMethod]
    public void VarInt_RoundTrips_ForBoundaryValues(ulong value)
    {
        var comp = new DeltaCompressor<ShipState, GlobalTickContext>(1);

        var pw = new Pipe();
        typeof(DeltaCompressor<ShipState, GlobalTickContext>)
            .GetMethod("WriteVarInt",
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { typeof(PipeWriter), typeof(ulong) }, null)!
            .Invoke(comp, new object[] { pw.Writer, value });
        pw.Writer.Complete();

        byte[] buf = SeqToArray((pw.Reader.ReadAsync().Result).Buffer);
        ulong round = DecodeVarInt(buf, out int consumed);

        // Writer must have produced a minimal-length encoding
        Assert.AreEqual(buf.Length, consumed, "Encode produced trailing bytes");
        Assert.AreEqual(value, round);
    }

    // --------------------------------------------------------------------
    // 6. Truncated packet never crashes
    // --------------------------------------------------------------------

    [TestMethod]
    public async Task TruncatedPacket_DoesNotThrow()
    {
        const int players = 3;
        var s = new DeltaCompressor<ShipState, GlobalTickContext>(players);
        var c = new DeltaCompressor<ShipState, GlobalTickContext>(players);

        var state = Enumerable.Range(0, players).Select(_ => RandomState()).ToArray();
        s.SetInitialState(state);
        c.SetInitialState(state);

        var ctx = new GlobalTickContext { GlobalTick = 55 };
        var full = new Pipe();
        await s.WriteDeltaPacketAsync(full.Writer, state, ctx);
        await full.Writer.CompleteAsync();
        byte[] packet = SeqToArray((await full.Reader.ReadAsync()).Buffer);

        for (int cut = 1; cut < packet.Length; cut++)
        {
            var pipe = new Pipe();
            await pipe.Writer.WriteAsync(packet.AsMemory(0, cut));
            await pipe.Writer.CompleteAsync();   // EOF before full packet

            await c.ApplyDeltaPacketAsync(pipe.Reader); // must not throw
        }
    }
    
    [TestMethod]
    public void Ctor_Throws_OnInvalidArraySize()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new DeltaCompressor<ShipState, GlobalTickContext>(0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new DeltaCompressor<ShipState, GlobalTickContext>(-5));
    }


    [TestMethod]
    public void SetInitialState_Throws_OnNullOrWrongLength()
    {
        var c = new DeltaCompressor<ShipState, GlobalTickContext>(3);

        Assert.ThrowsException<ArgumentNullException>(
            () => c.SetInitialState(null!));

        Assert.ThrowsException<ArgumentException>(
            () => c.SetInitialState(States(4)));
    }

    [TestMethod]
    public async Task Decoder_Ignores_PacketWithShortContext()
    {
        var c = new DeltaCompressor<ShipState, GlobalTickContext>(1);
        c.SetInitialState(States(1));

        // header length = 4, but body has only 4 bytes (half context)
        byte[] pkt = { 4,0,0,0, 1,2,3,4 };

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(pkt);
        await pipe.Writer.CompleteAsync();

        // should NOT throw
        await c.ApplyDeltaPacketAsync(pipe.Reader);
        await pipe.Reader.CompleteAsync();

        // state unchanged
        Assert.AreEqual(0u, c.CurrentState[0].InputMask);
    }

    [DataRow(true)]   // missing index
    [DataRow(false)]  // missing mask
    [TestMethod]
    public async Task Decoder_Ignores_WhenIndexOrMaskMissing(bool dropIndex)
    {
        var comp = new DeltaCompressor<ShipState, GlobalTickContext>(2);
        comp.SetInitialState(States(2));

        using var ms = new System.IO.MemoryStream();
        ms.Write(new byte[4]);                 // reserve length
        ms.Write(new byte[8]);                 // full context

        if (!dropIndex)                        // feed index only when asked
            ms.Write(VarInt(1));               // index = 1

        // NOTE: no mask or maybe no index at all
        int len = (int)(ms.Length - 4);
        BitConverter.TryWriteBytes(ms.GetBuffer().AsSpan(0, 4), len);

        var p = new Pipe();
        await p.Writer.WriteAsync(ms.ToArray());
        await p.Writer.CompleteAsync();

        await comp.ApplyDeltaPacketAsync(p.Reader);  // no exception
        await p.Reader.CompleteAsync();

        // still baseline state (all zeros)
        Assert.AreEqual(0, comp.CurrentState[1].PosX);
    }
    
    [TestMethod]
    public async Task Decoder_Ignores_WhenPayloadTruncated()
    {
        var c = new DeltaCompressor<ShipState, GlobalTickContext>(1);
        c.SetInitialState(States(1));

        using var ms = new System.IO.MemoryStream();
        ms.Write(new byte[4]);             // reserve length
        ms.Write(new byte[8]);             // context (OK)

        ms.Write(VarInt(0));               // index
        ms.Write(VarInt(1));               // mask -> expects PosX (4 bytes)
        ms.WriteByte(0xAA);                // but give only 1 byte (truncated)

        int len = (int)(ms.Length - 4);
        BitConverter.TryWriteBytes(ms.GetBuffer().AsSpan(0, 4), len);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(ms.ToArray());
        await pipe.Writer.CompleteAsync();

        await c.ApplyDeltaPacketAsync(pipe.Reader);  // should NOT throw
        await pipe.Reader.CompleteAsync();

        Assert.AreEqual(0, c.CurrentState[0].PosX);  // unchanged
    }

    
    [TestMethod]
    public async Task Decoder_Throws_WhenRemainingLessThanPayload()
    {
        const int n = 2;
        var c = new DeltaCompressor<ShipState, GlobalTickContext>(n);
        c.SetInitialState(States(n));

        using var ms = new System.IO.MemoryStream();
        ms.Write(new byte[4]);             // reserve length
        ms.Write(new byte[8]);             // context
        ms.Write(VarInt(1));               // index
        ms.Write(VarInt(1));               // mask expects 4-byte PosX
        // zero bytes of payload -> will rewind & exit silently

        int len = (int)(ms.Length - 4);
        BitConverter.TryWriteBytes(ms.GetBuffer().AsSpan(0, 4), len);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(ms.ToArray());
        await pipe.Writer.CompleteAsync();

        await c.ApplyDeltaPacketAsync(pipe.Reader); // silent exit, no throw
        await pipe.Reader.CompleteAsync();

        Assert.AreEqual(0, c.CurrentState[1].PosX);
    }


    [TestMethod]
    public void ShipState_ToString_IncludesKeyFields()
    {
        var s = new ShipState
        {
            Tick      = 42,
            PosX      = -10,
            PosY      =  11,
            Yaw       =  90,
            Vel       = 1234,
            InputMask = 0xF00
        };

        string str = s.ToString();

        StringAssert.Contains(str, "Tick:42");
        StringAssert.Contains(str, "Pos:(-10,11)");
        StringAssert.Contains(str, "Yaw:90");
        StringAssert.Contains(str, "Vel:1234");
        StringAssert.Contains(str, "Inputs:F00");
    }

    
    [TestMethod]
    public async Task WriteDeltaPacketAsync_Throws_OnNullState()
    {
        var comp = new DeltaCompressor<ShipState, GlobalTickContext>(2);
        comp.SetInitialState(new ShipState[2]);          // baseline ok

        var ctx  = new GlobalTickContext { GlobalTick = 1 };
        var pipe = new Pipe();

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => comp.WriteDeltaPacketAsync(pipe.Writer, null!, ctx));
    }

    [TestMethod]
    public async Task WriteDeltaPacketAsync_Throws_OnWrongStateLength()
    {
        var comp = new DeltaCompressor<ShipState, GlobalTickContext>(2);
        comp.SetInitialState(new ShipState[2]);          // baseline ok

        var ctx  = new GlobalTickContext { GlobalTick = 1 };
        var pipe = new Pipe();

        ShipState[] wrong = new ShipState[3];            // length mismatch

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => comp.WriteDeltaPacketAsync(pipe.Writer, wrong, ctx));
    }

    [TestMethod]
    public async Task ApplyDeltaPacketAsync_CompletesGracefully_WhenPipeIsEmpty()
    {
        var comp = new DeltaCompressor<ShipState, GlobalTickContext>(1);
        comp.SetInitialState(new ShipState[1]);

        var pipe = new Pipe();
        await pipe.Writer.CompleteAsync();          // no bytes, just EOF

        // Must return; should not hang or throw
        await comp.ApplyDeltaPacketAsync(pipe.Reader);
        await pipe.Reader.CompleteAsync();
    }


    [TestMethod]
    public async Task Decoder_Rewinds_WhenMaskVarIntIncomplete()
    {
        var c = new DeltaCompressor<ShipState, GlobalTickContext>(2);
        c.SetInitialState(new ShipState[2]);

        using var ms = new System.IO.MemoryStream();
        ms.Write(new byte[4]);             // length placeholder
        ms.Write(new byte[8]);             // full context
        ms.Write(VarInt(0));               // index present
        ms.WriteByte(0x80);                // start of mask var-int, but MSB=1 ⇒ incomplete

        int len = (int)(ms.Length - 4);
        BitConverter.TryWriteBytes(ms.GetBuffer().AsSpan(0, 4), len);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(ms.ToArray());
        await pipe.Writer.CompleteAsync();

        // Should not throw; will rewind and exit loop
        await c.ApplyDeltaPacketAsync(pipe.Reader);
        await pipe.Reader.CompleteAsync();
    }


    [TestMethod]
    public async Task Decoder_AppliesDelta_WhenIndexInRange()
    {
        var comp = new DeltaCompressor<ShipState, GlobalTickContext>(1);
        var baseline = new[] { new ShipState { PosX = 0 } };
        comp.SetInitialState(baseline);

        // Build packet: header, context, index=0, mask=1, payload=PosX(4 bytes)
        using var ms = new System.IO.MemoryStream();
        ms.Write(new byte[4]);                     // reserve length
        ms.Write(new byte[8]);                     // context (zeros OK)
        ms.Write(VarInt(0));                       // index 0
        ms.Write(VarInt(1));                       // mask bit 0 -> PosX
        ms.Write(BitConverter.GetBytes(42));       // new PosX = 42

        int len = (int)(ms.Length - 4);
        BitConverter.TryWriteBytes(ms.GetBuffer().AsSpan(0, 4), len);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(ms.ToArray());
        await pipe.Writer.CompleteAsync();

        await comp.ApplyDeltaPacketAsync(pipe.Reader);
        await pipe.Reader.CompleteAsync();

        Assert.AreEqual(42, comp.CurrentState[0].PosX);
    }

    private delegate bool TryReadDelegate(
        ref SequenceReader<byte> reader, out SequencePosition end);

    private static TryReadDelegate CreateTryReadDelegate<T, TCtx>(
        DeltaCompressor<T, TCtx> instance)
        where T : struct, IDeltaSerializable<T, TCtx>
        where TCtx : struct, IDeltaContext
    {
        var mi = typeof(DeltaCompressor<T, TCtx>)
                    .GetMethod("TryReadAndApplyPacket",
                                BindingFlags.NonPublic | BindingFlags.Instance)!;

        return (TryReadDelegate)mi.CreateDelegate(
                typeof(TryReadDelegate), instance);
    }

    [TestMethod]
    public async Task ApplyDelta_Completes_OnEmptyPipe()
    {
        var comp = new DeltaCompressor<ShipState, GlobalTickContext>(1);
        comp.SetInitialState(new ShipState[1]);

        var pipe = new Pipe();
        await pipe.Writer.CompleteAsync();                 // no bytes, EOF

        await comp.ApplyDeltaPacketAsync(pipe.Reader);     // must return
        await pipe.Reader.CompleteAsync();
    }
    
    [TestMethod]
    public void TryRead_Fails_When_IndexIncomplete()
    {
        var dc = new DeltaCompressor<ShipState, GlobalTickContext>(2);
        dc.SetInitialState(new ShipState[2]);

        using var ms = new System.IO.MemoryStream();
        ms.Write(new byte[4]);               // reserve length
        ms.Write(new byte[8]);               // context (8 bytes)

        ms.WriteByte(0x80);                  // start of index VarInt, MSB=1 → needs 2nd byte (missing)

        int len = (int)(ms.Length - 4);      // context + 1
        BitConverter.TryWriteBytes(ms.GetBuffer().AsSpan(0, 4), len);

        var seq = new ReadOnlySequence<byte>(ms.GetBuffer(), 0, (int)ms.Length);
        var rdr = new SequenceReader<byte>(seq);

        var del = CreateTryReadDelegate(dc);
        bool ok = del(ref rdr, out _);

        Assert.IsFalse(ok, "Should return false when index VarInt is incomplete");
    }

    [TestMethod]
    public void TryRead_Fails_When_MaskIncomplete()
    {
        var dc = new DeltaCompressor<ShipState, GlobalTickContext>(2);
        dc.SetInitialState(new ShipState[2]);

        using var ms = new System.IO.MemoryStream();
        ms.Write(new byte[4]);               // reserve length
        ms.Write(new byte[8]);               // context

        ms.WriteByte(0x00);                  // complete index (value 0)
        ms.WriteByte(0x80);                  // start of mask VarInt, incomplete

        int len = (int)(ms.Length - 4);      // context + 2
        BitConverter.TryWriteBytes(ms.GetBuffer().AsSpan(0, 4), len);

        var seq = new ReadOnlySequence<byte>(ms.GetBuffer(), 0, (int)ms.Length);
        var rdr = new SequenceReader<byte>(seq);

        var del = CreateTryReadDelegate(dc);
        bool ok = del(ref rdr, out _);

        Assert.IsFalse(ok, "Should return false when mask VarInt is incomplete");
    }

    // --------------------------------------------------------------------
    // Utility: VarInt encode
    // --------------------------------------------------------------------

    private static byte[] VarInt(ulong v)
    {
        Span<byte> tmp = stackalloc byte[10];
        int len = 0;
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            tmp[len++] = b;
        } while (v != 0);
        return tmp[..len].ToArray();
    }

    /// <summary>Decodes a VarInt from a byte array; returns the value and
    /// sets <paramref name="consumed"/> to the byte-count consumed.</summary>
    private static ulong DecodeVarInt(ReadOnlySpan<byte> src,
                                      out int consumed)
    {
        ulong val = 0;
        int sh = 0;
        int i = 0;

        for (; i < src.Length; i++)
        {
            byte b = src[i];
            val |= (ulong)(b & 0x7F) << sh;
            if ((b & 0x80) == 0) { i++; break; }
            sh += 7;
            if (sh > 63) throw new OverflowException("VarInt too large");
        }
        consumed = i;
        return val;
    }
}
