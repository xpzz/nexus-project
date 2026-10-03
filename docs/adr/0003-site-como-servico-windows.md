# ADR-0003 — Site como serviço do Windows (Kestrel) por padrão

- Data: 2026-10-03
- Situação: aceita
- Altera: ADR-0001 (site no IIS) e a premissa "porta própria e sem IIS" volta a valer por padrão

## Contexto

Com a conta única `svc.sccm` (ADR-0002), o pool do IIS não subia: pool de aplicativo exige o direito "Fazer logon como trabalho em lote",
que a GPO do domínio restringe. O Worker, que é serviço do Windows, subia normalmente (só precisa de "Fazer logon como serviço").

## Decisão

1. `hosting: "service"` (padrão): `AzulNexus.Web.exe` roda como serviço do Windows na mesma conta do Worker, com Kestrel escutando HTTPS na porta
   configurada. O certificado é lido de `LocalMachine\My` pela impressão digital gravada em `nexus.json`; o instalador concede à conta do serviço
   a leitura da chave privada. O pacote do site é *self-contained*: não exige runtime nem Hosting Bundle.
2. `hosting: "iis"` continua disponível para quem exigir IIS.
3. Ao trocar de `iis` para `service`, o instalador remove o site e o pool do Nexus no IIS para liberar a porta.

## Consequências

- Elimina o direito de lote, o IIS e o reinício do IIS (que afetava o management point e o distribution point do SCCM).
- O site passa a ter a mesma identidade do Worker (ADR-0002); o risco já aceito lá continua.
- Falhas de início (certificado ausente, sem acesso à chave, porta em uso) aparecem no log do site e no Log de Eventos com a mensagem
  "o que aconteceu, impacto e como resolver".
