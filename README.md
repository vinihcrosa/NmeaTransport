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
    EnableLogging = true,
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
- `EnableLogging` controls whether the client writes lifecycle, RX, and error messages to the terminal.
- `EnableLogging` defaults to `false`; `null` also keeps terminal logging disabled.
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
- `NmeaTcpClientOptions`: logging, checksum, reconnect, connect timeout, and write timeout settings
- `NmeaMessage`: structured header + payload representation
- `NmeaTcpServer`: lightweight TCP relay for valid NMEA sentences

## Development

Build and test locally with:

```bash
dotnet restore NmeaTransport.sln
dotnet build NmeaTransport.sln --no-restore --configuration Release
dotnet test NmeaTransport.sln --no-build --configuration Release
dotnet format NmeaTransport.sln --verify-no-changes
```

## Branching

The repository adopts Gitflow-style working branches for implementation work.

- `feature/<short-name>` for new features
- `fix/<short-name>` for bug fixes that do not require emergency release handling
- `hotfix/<short-name>` for urgent production fixes when applicable
- `release/<short-name>` for release-oriented branches when the workflow requires it

Prefer opening pull requests to `main` from one of these branch types. For new development tasks in this repository, default to `feature/...` unless the nature of the change clearly fits another type.

## CI and merge protection

Pull requests targeting `main` run a single required GitHub Actions workflow (for example, **Build and test**) that contains a job named `ci`. That job restores, builds, tests, and validates formatting with `dotnet format --verify-no-changes`.

To block merges when CI fails, configure the repository rules for `main` in GitHub:

1. Open `Settings` -> `Branches` or the repository rulesets page.
2. Edit the protection rule or ruleset for `main`.
3. Mark the status check **`Build and test / ci`** (workflow name / job id, as shown in the GitHub UI) as required.
4. Optionally require branches to be up to date before merging if you want fresh validation against the current `main`.

## Notes

- The package metadata is defined in [NmeaTransport.csproj](/Users/viniciusrosa/Documents/technomar/NmeaTransport/NmeaTransport/NmeaTransport.csproj).
- Release automation uses GitHub Actions plus `release-please`, with versioning persisted in the `.csproj`.
