# NMEA TCP Client

## Visao geral
`NmeaTcpClient` adiciona suporte de cliente TCP a biblioteca `NmeaTransport` para integracao com o `NmeaTcpServer` existente. O cliente trabalha com mensagens estruturadas (`NmeaMessage`), envia sentencas em ASCII com checksum calculado automaticamente e consome mensagens recebidas com roteamento por `Header`.

## API publica
- `NmeaTcpClient(string host, int port, NmeaTcpClientOptions? options = null)`
- `Task ConnectAsync(CancellationToken ct = default)`
- `Task DisconnectAsync()`
- `Task SendAsync(NmeaMessage message, CancellationToken ct = default)`
- `IDisposable RegisterHandler(string header, NmeaMessageHandler handler)`

### Tipos publicos
- `NmeaMessage`
  - `Prefix`
  - `Header`
  - `PayloadParts`
- `NmeaTcpClientOptions`
  - `EnableLogging`
  - `ValidateChecksum`
  - `ReconnectDelay`
  - `ConnectTimeout`
  - `WriteTimeout`

## Comportamento
- O cliente tenta a primeira conexao em `ConnectAsync`.
- Em caso de queda, o cliente tenta reconectar indefinidamente enquanto nao houver `DisconnectAsync`.
- Mensagens enviadas durante desconexao sao mantidas em fila e reenviadas depois da reconexao.
- Handlers registrados por `Header` permanecem ativos durante todo o ciclo de vida da instancia.
- `EnableLogging` controla os logs de ciclo de vida, RX e erro no terminal.
- `EnableLogging` tem default `false`; `null` tambem desabilita logging no terminal.
- A validacao obrigatoria sempre verifica o prefixo NMEA (`$` ou `!`).
- A validacao de checksum e opcional e controlada por `ValidateChecksum`.
- Mensagens invalidas sao descartadas e so sao registradas no `Console` quando `EnableLogging` estiver habilitado.

## Exemplo de uso
```csharp
using NmeaTransport.Clients;

var client = new NmeaTcpClient(
    "127.0.0.1",
    5000,
    new NmeaTcpClientOptions
    {
        EnableLogging = true,
        ValidateChecksum = true,
        ReconnectDelay = TimeSpan.FromSeconds(2),
        ConnectTimeout = TimeSpan.FromSeconds(1),
        WriteTimeout = TimeSpan.FromSeconds(1)
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
- O envio serializa a mensagem como `<prefix><header>,<payload>*<checksum>\r\n`, preservando o prefixo configurado.
- O checksum e calculado sobre o corpo da sentenca, sem incluir o inicializador e sem incluir o terminador de linha.
- O recebimento separa a sentenca em:
  - `Prefix`
  - `Header`
  - `PayloadParts`, obtido por `Split(',')`

## Documentacao de codigo
As classes e metodos publicos relevantes do cliente foram documentados com XML comments para apoiar geracao futura de documentacao por ferramentas como Doxygen ou pipelines equivalentes.
