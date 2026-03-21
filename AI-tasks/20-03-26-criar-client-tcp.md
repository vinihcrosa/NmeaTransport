# Relatorio da task: criar-client-tcp

## Resumo da implementacao
Foi adicionada uma implementacao reutilizavel de cliente TCP NMEA com reconexao automatica, fila de envio durante desconexao, roteamento de handlers por `Header`, validacao configuravel de checksum, testes automatizados e documentacao em `/docs`.

## Arquivos criados ou modificados
- `NmeaTransport/Clients/INmeaTcpClient.cs`
- `NmeaTransport/Clients/NmeaMessage.cs`
- `NmeaTransport/Clients/NmeaMessageHandler.cs`
- `NmeaTransport/Clients/NmeaTcpClient.cs`
- `NmeaTransport/Clients/NmeaTcpClientOptions.cs`
- `NmeaTransport/Internal/NmeaSentence.cs`
- `NmeaTransport/Server/NmeaTcpServer.cs`
- `NmeaTransport.Test/NmeaSentenceTests.cs`
- `NmeaTransport.Test/NmeaTcpClientIntegrationTests.cs`
- `NmeaTransport.Test/TestSupport/TcpIntegrationSupport.cs`
- `docs/nmea-tcp-client.md`
- `docs/AI-task-resume.md`
- remocao de `NmeaTransport/Class1.cs`

## Decisoes de design
- A API publica do cliente foi mantida enxuta: `ConnectAsync`, `DisconnectAsync`, `SendAsync` e `RegisterHandler`.
- O contrato de mensagem foi reduzido a `Header` e `PayloadParts`, sem parser semantico por tipo de sentenca.
- O envio usa uma fila interna unica para preservar ordem e permitir reenvio apos reconexao.
- A leitura e a escrita usam loops separados, com sessao TCP compartilhada e invalidadacao explicita em caso de falha.
- A logica de prefixo, checksum, parsing e serializacao foi centralizada em um helper interno reutilizado pelo cliente e pelo servidor.

## Assumptions e defaults
- A mesma instancia do cliente nao suporta multiplos ciclos arbitrarios completos de start/stop; apos `DisconnectAsync`, ela nao pode ser reiniciada.
- A validacao de checksum so e exigida quando `ValidateChecksum = true`.
- Prefixos validos iniciais: `$` e `!`.
- Timeouts padrao: conexao `1s`, escrita `1s`, reconexao `2s`, leitura sem timeout.
- Logs permanecem em `Console`, sem formato estruturado.

## Comandos executados
```bash
dotnet test --no-restore
```

## Limitacoes e observacoes
- O cliente sempre serializa mensagens enviadas com inicializador `$`.
- O contrato de recebimento continua generico; nao ha mapeamento para tipos especificos como `GGA` ou `RMC`.
- A observabilidade continua simples, baseada em `Console.WriteLine`.

## Resultado dos testes
`dotnet test --no-restore` executado com sucesso: 34 testes aprovados.
