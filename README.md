# OatIM.DeltaCompression

A generic, high-performance .NET library for delta-compressing arrays of state objects, designed for use with `System.IO.Pipelines`. This library is ideal for reducing network bandwidth in real-time applications like multiplayer games or data synchronization services.

---

## What It Solves

In many real-time applications, the same set of data (e.g., the position and velocity of all players) is sent over the network many times per second. Sending the complete data set every time is inefficient and consumes significant bandwidth, especially as the number of objects or the update frequency increases.

**Delta compression** solves this by sending only what has changed since the last update. Instead of sending the full state, the server calculates a small "delta" or patch. The client then applies this patch to its last known state to reconstruct the new state. This can lead to massive bandwidth savings.

## How It Works with `System.IO.Pipelines`

This library is built from the ground up to leverage `System.IO.Pipelines`, a high-performance I/O API in .NET. This provides several key advantages:

-   **Minimized Allocations:** `System.IO.Pipelines` uses pooled memory, which dramatically reduces memory allocations and garbage collection pressure—a critical factor in real-time applications.
-   **Zero-Copy Operations:** The library reads and writes directly to the pipeline's buffers, avoiding unnecessary and expensive data copies between memory locations.
-   **Backpressure Handling:** The pipeline system naturally handles backpressure, preventing a fast sender from overwhelming a slow receiver, which helps maintain application stability.

By combining delta compression with `System.IO.Pipelines`, this library offers a highly efficient solution for network serialization.

---

## Core Concepts

The library is built on a few key components that provide its flexibility and power.

-   `IDeltaSerializable<T, TContext>`: This is the core interface. Any `struct` you want to compress must implement this contract. It defines how your object calculates its own changes, applies patches, and interacts with a global context.
-   `IDeltaContext`: This interface defines a packet's global context. A context object (e.g., one containing the current server tick) is serialized once at the start of a packet and provides shared information to all state objects being deserialized.
-   `DeltaCompressor<T, TContext>`: This is the main engine. It orchestrates the entire process, comparing state arrays, using your `IDeltaSerializable` implementation to generate a delta packet, and applying received packets to update its internal state.

---

## Installation

You can install the package from the public NuGet gallery.

**.NET CLI**
```bash
dotnet add package OatIM.DeltaCompression
````

**Package Manager**

```powershell
Install-Package OatIM.DeltaCompression
```

-----

## Quick Start Guide

Here’s how to integrate the library by implementing `IDeltaSerializable` for a `ShipState` struct, method by method.

### Step 1: Define Your State Struct

First, define the `struct` that holds your data. A private `[Flags]` enum is a clean and efficient way to represent which fields can change.

```csharp
// ShipState.cs
using OatIM.DeltaCompression;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

public struct ShipState : IDeltaSerializable<ShipState, GlobalTickContext>
{
    [Flags]
    private enum ChangeMask : ulong
    {
        None      = 0,
        PosX      = 1 << 0,
        PosY      = 1 << 1,
        Yaw       = 1 << 2,
    }

    public int PosX;
    public int PosY;
    public ushort Yaw;
    public ulong LastUpdatedTick; // This will be set from the context

    // Interface methods will go here...
}
```

### Step 2: Implement `GetChangeMask`

This method is the heart of the delta calculation. It compares the `oldState` to the current state and builds a bitmask of every field that has changed. The `DeltaCompressor` uses this mask to determine if an update is needed at all.

```csharp
public ulong GetChangeMask(ShipState oldState, GlobalTickContext context)
{
    ChangeMask mask = ChangeMask.None;
    if (PosX != oldState.PosX) mask |= ChangeMask.PosX;
    if (PosY != oldState.PosY) mask |= ChangeMask.PosY;
    if (Yaw != oldState.Yaw) mask |= ChangeMask.Yaw;
    return (ulong)mask;
}
```

### Step 3: Implement `WriteDelta`

This method serializes only the changed fields to the network stream. It checks which bits are set in the `changeMask` and writes the corresponding property values to the `PipeWriter`.

```csharp
public void WriteDelta(ref PipeWriter writer, ulong changeMask)
{
    var mask = (ChangeMask)changeMask;
    if (mask.HasFlag(ChangeMask.PosX))
    {
        var span = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(span, PosX);
        writer.Advance(sizeof(int));
    }
    if (mask.HasFlag(ChangeMask.PosY))
    {
        var span = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(span, PosY);
        writer.Advance(sizeof(int));
    }
    if (mask.HasFlag(ChangeMask.Yaw))
    {
        var span = writer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(span, Yaw);
        writer.Advance(sizeof(ushort));
    }
}
```

### Step 4: Implement `ApplyDelta`

This is the inverse of `WriteDelta`. On the receiving end, this method reads the values for the changed fields from the `SequenceReader<byte>` and applies them to the struct's properties.

```csharp
public void ApplyDelta(ref SequenceReader<byte> reader, ulong changeMask)
{
    var mask = (ChangeMask)changeMask;
    if (mask.HasFlag(ChangeMask.PosX))
    {
        reader.TryReadLittleEndian(out int val);
        PosX = val;
    }
    if (mask.HasFlag(ChangeMask.PosY))
    {
        reader.TryReadLittleEndian(out int val);
        PosY = val;
    }
    if (mask.HasFlag(ChangeMask.Yaw))
    {
        reader.TryReadLittleEndian(out short val); // Read as signed
        Yaw = (ushort)val; // Cast to unsigned
    }
}
```

### Step 5: Implement `GetDeltaSize`

This method is crucial for the `DeltaCompressor` to validate that a received packet is complete before trying to parse it. It calculates the exact byte size of a delta payload based on a given `changeMask`.

```csharp
public int GetDeltaSize(ulong changeMask)
{
    var mask = (ChangeMask)changeMask;
    int size = 0;
    if (mask.HasFlag(ChangeMask.PosX)) size += sizeof(int);
    if (mask.HasFlag(ChangeMask.PosY)) size += sizeof(int);
    if (mask.HasFlag(ChangeMask.Yaw)) size += sizeof(ushort);
    return size;
}
```

### Step 6: Implement `ApplyContext`

This method applies the global packet context to the state object. It's called on *every* object in the array, even those that had no other changes, ensuring global data like a tick is always synchronized.

```csharp
public void ApplyContext(GlobalTickContext context)
{
    this.LastUpdatedTick = context.GlobalTick;
}
```

### Step 7: Define Your Context

Create a struct that implements `IDeltaContext` to hold any global data for the packet.

```csharp
// GlobalTickContext.cs
public struct GlobalTickContext : IDeltaContext
{
    public ulong GlobalTick { get; set; }

    public int Size => sizeof(ulong);

    public void Write(ref PipeWriter writer)
    {
        var span = writer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(span, GlobalTick);
        writer.Advance(sizeof(ulong));
    }

    public void Read(ref SequenceReader<byte> reader)
    {
        reader.TryReadLittleEndian(out long val);
        GlobalTick = (ulong)val;
    }
}
```

### Step 8: Use the `DeltaCompressor`

Finally, use the `DeltaCompressor` on your server and client to manage the synchronization process.

**On the Server (Sending Updates):**

```csharp
// During setup
var serverCompressor = new DeltaCompressor<ShipState, GlobalTickContext>(MAX_PLAYERS);
serverCompressor.SetInitialState(initialGameStates);

// In your game loop
async Task SendUpdatesToClient(PipeWriter clientPipeWriter)
{
    var latestStates = GetCurrentShipStates();
    var context = new GlobalTickContext { GlobalTick = currentServerTick };

    // This creates the delta packet and writes it to the client's network pipe.
    await serverCompressor.WriteDeltaPacketAsync(clientPipeWriter, latestStates, context);
}
```

**On the Client (Receiving Updates):**

```csharp
// During setup
var clientCompressor = new DeltaCompressor<ShipState, GlobalTickContext>(MAX_PLAYERS);
clientCompressor.SetInitialState(initialGameStates); // Received from a keyframe

// In your network event handler
async Task OnDataReceived(PipeReader networkReader)
{
    // This reads the delta packet and updates the internal state array.
    await clientCompressor.ApplyDeltaPacketAsync(networkReader);

    // The state is now synchronized and ready for rendering.
    var latestStates = clientCompressor.CurrentState;
    RenderFrame(latestStates);
}
```

> **Note:** For a complete, working example of a `struct` and `context` implementation, see the [`ShipState.cs`](tests/States/ShipState.cs) and [`GlobalTickContext.cs`](tests/Contexts/GlobalTickContext.cs) files in the test project.

-----

## Building from Source

To build the library yourself, clone the repository and use the .NET CLI.

```bash
# Clone the repository
git clone https://github.com/oat-im/deltacompression.git
cd deltacompression

# Restore dependencies and build in Release configuration
dotnet build --configuration Release
```

## Running Tests

The solution includes a comprehensive test suite. To run the tests:

```bash
dotnet test
```

## License

This project is licensed under the **MIT License**. See the LICENSE file for details.