# NmeaTransport

`NmeaTransport` is a small .NET library for exchanging NMEA sentences over TCP.

It currently provides:

- a reusable TCP client with automatic reconnection
- structured message sending via `NmeaMessage`
- header-based message handlers for incoming sentences
- a lightweight TCP server that validates and broadcasts NMEA sentences

## Target framework

The package targets `netstandard2.1`.

## Installation

```bash
dotnet add package NmeaTransport
```

## Client usage

The client connects to a remote TCP endpoint, keeps a background lifecycle running, routes incoming messages by header, and can queue outgoing messages while disconnected.

```csharp
using NmeaTransport.Clients;

var options = new NmeaTcpClientOptions
{
    ValidateChecksum = true,
    ReconnectDelay = TimeSpan.FromSeconds(2),
    ConnectTimeout = TimeSpan.FromSeconds(1),
    WriteTimeout = TimeSpan.FromSeconds(1)
};

await using var client = new NmeaTcpClient("127.0.0.1", 10110, options);

using var registration = client.RegisterHandler("GPGLL", async (message, cancellationToken) =>
{
    Console.WriteLine($"Received {message.Header}: {string.Join(", ", message.PayloadParts)}");
    await Task.CompletedTask;
});

await client.ConnectAsync();

await client.SendAsync(new NmeaMessage(
    "GPGLL",
    ["4916.45", "N", "12311.12", "W", "225444", "A", ""]));

await client.DisconnectAsync();
```

### Client behavior

- `ConnectAsync()` starts the client lifecycle and waits for the first successful connection.
- `DisconnectAsync()` stops the lifecycle and prevents further reconnect attempts.
- `SendAsync()` serializes the message as a NMEA sentence with checksum.
- `RegisterHandler()` routes incoming messages by header and returns an `IDisposable` to unregister.
- When disconnected unexpectedly, the client keeps retrying based on `ReconnectDelay`.
- Outgoing messages remain queued and are flushed after reconnection.

## Server usage

The server listens for TCP clients, accepts valid NMEA sentences, and broadcasts them to connected peers.

```csharp
using NmeaTransport.Server;

await using var server = new NmeaTcpServer(10110);
using var cts = new CancellationTokenSource();

var runTask = server.StartAsync(cts.Token);

Console.WriteLine("Server is running. Press enter to stop.");
Console.ReadLine();

await server.StopAsync();
await runTask;
```

### Server behavior

- ignores invalid or malformed input
- validates checksum when the incoming sentence includes one
- broadcasts valid sentences to all connected clients

## Core types

- `NmeaTcpClient`: main client implementation
- `INmeaTcpClient`: public client contract
- `NmeaTcpClientOptions`: checksum, reconnect, connect timeout, and write timeout settings
- `NmeaMessage`: structured header + payload representation
- `NmeaTcpServer`: lightweight TCP relay for valid NMEA sentences

## Development

Build and test locally with:

```bash
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

## Notes

- The package metadata is defined in [NmeaTransport.csproj](/Users/viniciusrosa/Documents/technomar/NmeaTransport/NmeaTransport/NmeaTransport.csproj).
- Release automation uses GitHub Actions plus `release-please`, with versioning persisted in the `.csproj`.
