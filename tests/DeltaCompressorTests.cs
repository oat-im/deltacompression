using Oat.IO.DeltaCompression.Tests.Contexts;
using Oat.IO.DeltaCompression.Tests.States;
using System.IO.Pipelines;

namespace Oat.IO.DeltaCompression.Tests;

[TestClass]
public class DeltaCompressorTests
{
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
        for(int i = 0; i < serverStateTick2.Length; i++)
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
            Assert.AreEqual(expected.Tick, actual.Tick, $"Tick mismatch for player {i}");
            Assert.AreEqual(expected.PosX, actual.PosX, $"PosX mismatch for player {i}");
            Assert.AreEqual(expected.PosY, actual.PosY, $"PosY mismatch for player {i}");
            Assert.AreEqual(expected.Yaw, actual.Yaw, $"Yaw mismatch for player {i}");
            Assert.AreEqual(expected.Vel, actual.Vel, $"Vel mismatch for player {i}");
            Assert.AreEqual(expected.InputMask, actual.InputMask, $"InputMask mismatch for player {i}");
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
        Assert.AreEqual(context.Size, result.Buffer.Length);
    }
}
