# Azul Nexus — guia para o Claude Code

Especificação: `docs/SPEC.md`. Decisões: `docs/adr/` (ADR-0001: sem instalador, `dotnet publish`; ADR-0002: conta única; ADR-0003: site como serviço do Windows por padrão, IIS opcional; ADR-0004: reconciliação de dispositivos).
Código e identificadores em inglês; interface, mensagens e documentação em português do Brasil.
Toda mensagem de erro: o que aconteceu, impacto e como resolver (`Nexus.Core.Errors.ErrorCatalog`).
Nunca conectar a SCCM, Intune, Entra ID ou AD reais: use o modo simulado (`SourceMode.Simulated`, `Nexus.Simulation`).

## Comandos

```bash
dotnet build AzulNexus.slnx
dotnet test AzulNexus.slnx

# Pacote de implantação (IIS) — gera artifacts/AzulNexus-<versão>/ e .zip
pwsh deploy/Publish-AzulNexus.ps1
pwsh deploy/tests/Common.Tests.ps1        # testes das funções de decisão dos scripts (também em powershell.exe 5.1 no CI)
pwsh deploy/tests/Azure.Tests.ps1         # provisionamento do Entra ID contra um Graph simulado
pwsh deploy/tests/Syntax.Tests.ps1        # BOM e sintaxe de todos os .ps1
# No servidor (admin): deploy/Install-AzulNexus.ps1 [-DetectOnly], deploy/Uninstall-AzulNexus.ps1 — veja docs/guias/instalacao-iis.md

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
- `Nexus.Collectors.Graph` — leitor somente leitura do Microsoft Graph (Intune e Entra), certificado do app coletor (nunca a conta svc.sccm)
- `Nexus.Reconciliation` — ativo único (regras da ADR-0004), KPIs de cobertura e persistência
- `Nexus.Simulation` — dados sintéticos determinísticos e script do banco SCCM simulado
- `Nexus.Worker` — serviço Windows: agendador, coletas, verificações (rodam com a conta do serviço), limites de CPU
- `Nexus.Web` — site no IIS: assistente, saúde, acesso por código de configuração
- `Nexus.Cli` — `nexusctl`
- `deploy/Install-AzulNexusAzure.ps1` + `lib/Azure.ps1` — Entra ID/Intune: registros, certificados, consentimento, funções (`config/azure.json`, lido por `AzureSettingsStore`)
- `deploy/` — scripts de publicação, instalação, atualização e remoção no IIS (PowerShell 5.1+; lógica testável em `deploy/lib/Common.ps1`)
