# ADR-0001 — Implantação por `dotnet publish` no IIS, sem instalador

- Data: 2026-10-03
- Situação: aceita (pedido do responsável pelo produto)
- Substitui: SPEC §3.1, §3.2, §3.5 (Setup.exe), §5.1 (linha "Instalador": MSI/WiX/WPF) e a parte "sem IIS" das premissas 1, 8 e §5.2

## Decisão

1. Não há MSI, WiX, assistente WPF nem `Setup.exe`. O pacote é a saída de `dotnet publish` (win-x64, dependente de framework):
   `web/` (site no IIS), `worker/` (serviço Windows), `cli/` (`nexusctl`) e scripts PowerShell de instalação.
2. **AzulNexus.Web** roda no IIS (ASP.NET Core Module V2, *in-process*), em site e pool de aplicativos próprios
   (`Azul Nexus` / `AzulNexus`, identidade `IIS AppPool\AzulNexus` ou gMSA), em porta própria (padrão 8443), sem tocar nos sites do SCCM.
3. **AzulNexus.Worker** continua como serviço Windows (conta virtual `NT SERVICE\AzulNexus.Worker` ou gMSA): coleta não pode depender de reciclagem de pool.
4. Instalar, atualizar e reparar = rodar o mesmo script idempotente (`Install-AzulNexus.ps1`) com o arquivo `install.json`.
   O script não instala o ASP.NET Core Hosting Bundle sozinho, porque isso reinicia o IIS (afeta management point e distribution point do SCCM):
   exige caminho do instalador offline e confirmação explícita (`-AllowIisRestart`).
5. Banco do Nexus: SQL Server ou PostgreSQL existentes (sem PostgreSQL embarcado, que dependia do instalador).

## Consequências

- Pré-requisitos no servidor: IIS (já presente em servidores do SCCM com MP/DP), recurso WebSockets e ASP.NET Core 10 Hosting Bundle.
  Isso contraria a premissa 1 (“nada a instalar antes”); é aceito por esta decisão.
- O IIS passa a ser compartilhado com funções do SCCM; o isolamento é por pool, porta e identidade próprios.
- Os critérios de aceite da Fase 0 que citam “Avançar” e MSI passam a valer para o script: instalação silenciosa, reparo por reexecução,
  atualização com backup antes da migração e desinstalação preservando dados.

## Atualização (ADR-0003)

O site também pode rodar como **serviço do Windows com Kestrel** (`hosting: "service"`, agora o padrão), porque o pool do IIS exige o direito
"Fazer logon como trabalho em lote", que GPO de domínio costuma restringir para contas de serviço. Nesse modo não há IIS nem Hosting Bundle.
O modo IIS continua disponível (`hosting: "iis"`).
