using OatIM.DeltaCompression.Tests.Contexts;
using OatIM.DeltaCompression.Tests.States;
using System.IO.Pipelines;

namespace OatIM.DeltaCompression.Tests;

public class DeltaCompressorTests
{
    [Fact]
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
            Assert.Equal(expected.Tick, actual.Tick);
            Assert.Equal(expected.PosX, actual.PosX);
            Assert.Equal(expected.PosY, actual.PosY);
            Assert.Equal(expected.Yaw, actual.Yaw);
            Assert.Equal(expected.Vel, actual.Vel);
            Assert.Equal(expected.InputMask, actual.InputMask);
        }
    }

    [Fact]
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
        Assert.True(buffer.Length > 0, "Buffer should not be empty");
        Assert.Equal(GlobalTickContext.Size + sizeof(uint), buffer.Length);
    }

    [Fact]
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
        await Assert.ThrowsAsync<OverflowException>(async () =>
        {
            var reader = pipe.Reader;
            await compressor.ApplyDeltaPacketAsync(reader);
        });
    }
}
