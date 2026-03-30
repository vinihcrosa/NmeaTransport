# NMEA UDP Client

## Visao geral
`NmeaUdpClient` adiciona suporte de cliente UDP a biblioteca `NmeaTransport` para cenarios baseados em datagrama. O cliente trabalha com mensagens estruturadas (`NmeaMessage`), envia sentencas em ASCII com checksum calculado automaticamente, escuta continuamente em uma porta UDP local e roteia mensagens recebidas por `Header`.

## API publica
- `NmeaUdpClient(int port, NmeaUdpClientOptions? options = null)`
- `Task ConnectAsync(CancellationToken ct = default)`
- `Task DisconnectAsync()`
- `Task SendAsync(NmeaMessage message, CancellationToken ct = default)`
- `Task SendAsync(NmeaMessage message, IPEndPoint remoteEndPoint, CancellationToken ct = default)`
- `Task SendBroadcastAsync(NmeaMessage message, CancellationToken ct = default)`
- `IDisposable RegisterHandler(string header, NmeaMessageHandler handler)`

### Tipos publicos
- `INmeaUdpClient`
- `NmeaUdpClientOptions`
  - `EnableLogging`
  - `ValidateChecksum`
  - `ReceiveTimeout`
  - `WriteTimeout`
  - `DefaultRemoteEndPoint`
  - `BroadcastAddress`
- `NmeaMessage`
  - `Prefix`
  - `Header`
  - `PayloadParts`

## Comportamento
- `ConnectAsync` cria o socket UDP, faz bind na porta local configurada e inicia a escuta continua.
- `DisconnectAsync` interrompe a escuta, fecha o socket e impede restart da mesma instancia.
- `SendAsync(message)` usa o endpoint remoto padrao configurado em `DefaultRemoteEndPoint`.
- `SendAsync(message, remoteEndPoint)` envia um datagrama UDP para o destino informado.
- `SendBroadcastAsync(message)` envia um datagrama UDP broadcast usando a mesma porta configurada no cliente.
- `EnableLogging` controla logs de ciclo de vida, RX, TX e erros no terminal.
- `EnableLogging` tem default `false`; `null` tambem desabilita logging no terminal.
- A validacao obrigatoria sempre verifica o prefixo NMEA (`$` ou `!`).
- A validacao de checksum e opcional e controlada por `ValidateChecksum`.
- Mensagens invalidas sao descartadas e so sao registradas no `Console` quando `EnableLogging` estiver habilitado.
- Handlers registrados por `Header` permanecem ativos durante todo o ciclo de vida da instancia.

## Exemplo de uso
```csharp
using System.Net;
using NmeaTransport.Clients;

var client = new NmeaUdpClient(
    5000,
    new NmeaUdpClientOptions
    {
        EnableLogging = true,
        ValidateChecksum = true,
        ReceiveTimeout = TimeSpan.FromSeconds(1),
        WriteTimeout = TimeSpan.FromSeconds(1),
        DefaultRemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5001),
        BroadcastAddress = IPAddress.Broadcast
    });

using var registration = client.RegisterHandler("GPGLL", async (message, ct) =>
{
    Console.WriteLine($"{message.Header}: {string.Join(", ", message.PayloadParts)}");
    await Task.CompletedTask;
});

await client.ConnectAsync();

await client.SendAsync(new NmeaMessage("GPGLL", new[]
{
    "4916.45",
    "N",
    "12311.12",
    "W",
    "225444",
    "A",
    string.Empty
}));

await client.SendBroadcastAsync(new NmeaMessage("GPRMC", new[]
{
    "1",
    "2",
    "3"
}));

await client.SendAsync(new NmeaMessage("AIVDM", new[]
{
    "1",
    "1",
    string.Empty,
    "A",
    "15MvqR0P00PD;88MD5MTDwvN0<0u",
    "0"
}, '!'));

await client.DisconnectAsync();
```

## Validacao e serializacao
- `NmeaMessage` usa `$` como prefixo padrao e tambem aceita `!` quando informado explicitamente.
- O envio serializa a mensagem como `<prefix><header>,<payload>*<checksum>`.
- O checksum e calculado sobre o corpo da sentenca, sem incluir o inicializador.
- O recebimento aceita datagramas com ou sem `\r\n` no final.
- O recebimento separa a sentenca em:
  - `Prefix`
  - `Header`
  - `PayloadParts`, obtido por `Split(',')`

## Documentacao de codigo
As classes e metodos publicos relevantes do cliente foram documentados com XML comments para apoiar geracao futura de documentacao por ferramentas ou pipelines equivalentes.
