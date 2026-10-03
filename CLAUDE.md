# Azul Nexus — guia para o Claude Code

Especificação: `docs/SPEC.md`. Decisões: `docs/adr/` (a ADR-0001 troca o instalador por `dotnet publish` + IIS).
Código e identificadores em inglês; interface, mensagens e documentação em português do Brasil.
Toda mensagem de erro: o que aconteceu, impacto e como resolver (`Nexus.Core.Errors.ErrorCatalog`).
Nunca conectar a SCCM, Intune, Entra ID ou AD reais: use o modo simulado (`SourceMode.Simulated`, `Nexus.Simulation`).

## Comandos

```bash
dotnet build AzulNexus.slnx
dotnet test AzulNexus.slnx

# Nova migração (sempre nos dois provedores)
dotnet tool restore
cd src/Nexus.Data
dotnet ef migrations add <Nome> --context SqlServerNexusDbContext --output-dir Migrations/SqlServer --namespace Nexus.Data.Migrations.SqlServer
dotnet ef migrations add <Nome> --context PostgresNexusDbContext --output-dir Migrations/Postgres --namespace Nexus.Data.Migrations.Postgres

# Execução local em modo demonstração (PostgreSQL local)
export NEXUS_DATA_DIR=$PWD/.nexus-data NEXUS_DB_PASSWORD=<senha-dev>
dotnet src/Nexus.Cli/bin/Debug/net10.0/nexusctl.dll configure --from <install.resolved.json>   # "demoMode": true
dotnet src/Nexus.Cli/bin/Debug/net10.0/nexusctl.dll migrate
dotnet run --project src/Nexus.Worker
dotnet run --project src/Nexus.Web
```

## Estrutura

- `Nexus.Core` — configuração, catálogo de erros, saúde, código de configuração, permissões do Graph
- `Nexus.Data` — EF Core (SQL Server e PostgreSQL), fila de comandos Web→Worker, modo de configuração, diagnóstico
- `Nexus.Collectors.Sccm` / `Nexus.Collectors.ActiveDirectory` — leitores somente leitura e scripts de concessão
- `Nexus.Simulation` — dados sintéticos determinísticos e script do banco SCCM simulado
- `Nexus.Worker` — serviço Windows: agendador, coletas, verificações (rodam com a conta do serviço), limites de CPU
- `Nexus.Web` — site no IIS: assistente, saúde, acesso por código de configuração
- `Nexus.Cli` — `nexusctl`
