# Criar client UDP

## Resumo da implementacao
- Foi adicionado um cliente UDP reutilizavel com escuta continua em porta local, envio unicast, envio broadcast e roteamento por `Header`.
- A implementacao reaproveita `NmeaMessage`, `NmeaMessageHandler` e `NmeaSentence`.
- Foi criada documentacao publica dedicada para o cliente UDP e cobertura automatizada para os principais cenarios.

## Arquivos criados e modificados
- `NmeaTransport/Clients/INmeaUdpClient.cs`
- `NmeaTransport/Clients/NmeaUdpClient.cs`
- `NmeaTransport/Clients/NmeaUdpClientOptions.cs`
- `NmeaTransport.Test/NmeaUdpClientIntegrationTests.cs`
- `NmeaTransport.Test/TestSupport/UdpIntegrationSupport.cs`
- `docs/nmea-udp-client.md`
- `README.md`

## Decisoes de design adotadas
- A API publica segue `ConnectAsync` e `DisconnectAsync` para manter ergonomia consistente com o cliente TCP.
- O cliente UDP usa um tipo de options dedicado, sem carregar configuracoes que so fazem sentido em TCP.
- O envio UDP nao usa fila nem reconexao automatica.
- Broadcast usa `IPAddress.Broadcast` por default e a mesma porta configurada no cliente.

## Assumptions e defaults usados
- O transporte continua em ASCII.
- O datagrama enviado nao inclui `\r\n`; o recebimento aceita terminador opcional.
- `SendAsync(message)` depende de `DefaultRemoteEndPoint` configurado.

## Comandos executados para validacao
- `dotnet test`

## Limitacoes, riscos ou pendencias
- O teste de broadcast depende de comportamento local de socket com `ReuseAddress`, o que pode variar mais do que cenarios de loopback unicast.
- A implementacao atual fica em IPv4 para manter broadcast simples e previsivel.

## Status final dos testes
- `dotnet test` executado com sucesso.
- Resultado final: 53 testes aprovados, 0 falhas, 0 ignorados.
