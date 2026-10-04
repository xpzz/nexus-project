# Azul Nexus — guia para o Claude Code

Especificação: `docs/SPEC.md`. Decisões: `docs/adr/` (ADR-0001: sem instalador, `dotnet publish`; ADR-0002: conta única; ADR-0003: site como serviço do Windows por padrão, IIS opcional; ADR-0004: reconciliação de dispositivos; ADR-0005: acesso aberto às telas de inventário sem SSO; ADR-0006: pool de máquinas ativas; ADR-0007: motor de evidências; ADR-0008: papéis e login com Entra ID; ADR-0009: histórico e evidências brutas; ADR-0010: interface e tema). Visão geral em `docs/arquitetura.md`; diagnóstico do estado anterior em `docs/diagnostico-projeto-atual.md`; o que depende de fontes não conectadas em `docs/pendencias-fontes.md`.
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

# Interface no navegador (site em modo demonstração, https://localhost:8443; precisa de Playwright e, para a auditoria, do axe-core)
PLAYWRIGHT_MODULE=<caminho>/playwright/index.mjs node tests/ui/theme.mjs     # tema: SO, escolha explícita, navegação, voltar, rota direta
PLAYWRIGHT_MODULE=<caminho>/playwright/index.mjs node tests/ui/flows.mjs     # KPI = lista, filtros, filtros salvos, exportação, exceções, teclado
AXE=<caminho>/axe.min.js PLAYWRIGHT_MODULE=<caminho>/playwright/index.mjs node tests/ui/audit.mjs   # acessibilidade e rolagem, claro e escuro, três resoluções

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
- `Nexus.Reconciliation` — ativo único (regras da ADR-0004), **motor de evidências** (`EvidenceEngine`: estados, score explicável, limiares por tipo, ADR-0007), `ExecutiveSummary` (KPIs da visão executiva e fila de atenção), `MamReport` e `ProtectionControls` (MAM, BYOD, Edge), `SourceComparison`, `AssetHistory`, KPIs de cobertura e persistência
- `Nexus.Simulation` — dados sintéticos determinísticos e script do banco SCCM simulado
- `Nexus.Worker` — serviço Windows: agendador, coletas, verificações (rodam com a conta do serviço), limites de CPU
- `Nexus.Web` — site (serviço do Windows ou IIS), seis áreas: Visão executiva, Parque ativo (+ Comparar fontes), Inventário (colunas, filtros e filtros salvos na URL, exportação auditada), Governança Microsoft, MAM e BYOD, Qualidade dos dados, Operações (fontes, execuções por Run ID, coleta manual); assistente, saúde e acesso por código de configuração. Papéis e login: `Setup/NexusRoles.cs`, `Setup/EntraSignIn.cs`. Tema: `wwwroot/theme.js` + tokens `light-dark()` em `app.css`. Regras, índice e KPIs em `Nexus.Reconciliation` (`HealthModel`, `Overview`, `InventoryQuery`); regras sem dado coletado aparecem como "Aguardando coleta", nunca como zero
- Coletas (jobs do Worker, em ordem): `sccm.devices` (hardware e datas do cliente, com fallback), `ad.computers`, `intune.devices`, `entra.devices`, `intune.mam` (registros de proteção de apps), `entra.users` (área e conta habilitada), `intune.policies` (catálogo e estado por dispositivo, via `$batch`), `intune.apppolicies` (proteção de apps com controles e configuração do Edge), `entra.ca` (Acesso Condicional), `entra.signins` (acesso ao M365 por dispositivo; exige AuditLog.Read.All e Entra ID P1), `xdr.endpoints` (tabela do Cortex XDR, só leitura; `nexusctl xdr-configure`), `netskope.clients` (API do tenant; `nexusctl netskope-configure` e `netskope-test`; proxy com `nexusctl network-proxy`), `inventory.reconcile`. Software instalado é lido sob demanda (comando `FetchInventory`)
- Atividade: o estado operacional vem do `EvidenceEngine` (7 estados, limiares por tipo em `Evidence` do `nexus.json`); o pool legado (`ActivityModel`) continua como sinal e cruza as datas de SCCM, Intune, XDR, Netskope, MAM, Entra e AD; fonte forte sozinha ou duas fontes colocam a máquina no pool; AD ou Entra sozinhos ficam "não confirmados"
- `Nexus.Cli` — `nexusctl`
- `deploy/Install-AzulNexusAzure.ps1` + `lib/Azure.ps1` — Entra ID/Intune: registros, certificados, consentimento, funções (`config/azure.json`, lido por `AzureSettingsStore`)
- `deploy/` — scripts de publicação, instalação, atualização e remoção no IIS (PowerShell 5.1+; lógica testável em `deploy/lib/Common.ps1`)
