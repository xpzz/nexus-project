# Configuração e implantação no servidor do SCCM

Resumo operacional. O passo a passo com telas está em [tutorial-instalacao-servidor.md](tutorial-instalacao-servidor.md); aqui ficam o que o instalador faz, as variáveis, as contas, a execução local, a publicação, o proxy, a operação sem internet e o rollback.

## 1. Desenho

| Peça | Onde | Como |
|---|---|---|
| Web | Servidor do SCCM | Serviço do Windows (Kestrel, HTTPS) por padrão; IIS é opcional (ADR-0003). |
| Worker | Servidor do SCCM | Serviço do Windows: agendador, coletas, verificações, limites de CPU. |
| Banco do Nexus | SQL Server (preferencial) ou PostgreSQL | **Banco próprio e separado** das bases de origem (`AzulNexus`). |
| SCCM | Banco do site (`CM_PMS` neste ambiente: `sccm.database`) | Somente leitura, papel `azul_nexus_reader` só com `SELECT` nas views listadas em `SccmViews`. |
| Conta | `svc.sccm` | A mesma para Web e Worker (ADR-0002). Não é usada para o Azure. |
| Azure | Aplicativo "Azul Nexus – Coletor" | Certificado; nunca a conta de serviço. |

As views lidas do SCCM: `v_R_System`, `v_CH_ClientSummary`, `v_CH_EvalResults`, `v_GS_COMPUTER_SYSTEM`, `v_GS_PC_BIOS`, `v_GS_SYSTEM_ENCLOSURE`, `v_GS_COMPUTER_SYSTEM_PRODUCT`, `v_GS_OPERATING_SYSTEM`, `v_GS_PROCESSOR`, `v_GS_X86_PC_MEMORY`, `v_GS_LOGICAL_DISK`, `v_GS_ADD_REMOVE_PROGRAMS(_64)`, `v_GS_WORKSTATION_STATUS`, `v_RA_System_MACAddresses`, `v_RA_System_IPAddresses`, entre outras. Se uma view não puder ser lida, só o que depende dela fica "indisponível" (MAC, IP e chassi são lidos à parte); rode `nexusctl sccm-grant` para liberar as novas.

## 2. Variáveis e arquivos

| Item | Para quê |
|---|---|
| `NEXUS_DATA_DIR` | Pasta de dados (`config`, `logs`, `keys`, `backups`, `scripts`). Padrão no servidor: definido pelo instalador no registro. |
| `NEXUS_DB_PASSWORD` | Senha do PostgreSQL (SQL Server usa autenticação integrada). Nunca vai para `nexus.json` em texto. |
| `NEXUS_NETSKOPE_TOKEN` | Token do Netskope (alternativa ao token protegido por DPAPI). |
| `config/nexus.json` | Configuração sem segredos. Seções novas abaixo. |
| `config/azure.json` | Registros do Azure (IDs, impressões digitais, funções). Sem segredo. |

Seções de `nexus.json` para o inventário reconciliado:

```jsonc
"evidence": {                       // ADR-0007: nenhuma data fica no código
  "default": { "confirmedDays": 7, "probableDays": 30, "noRecentDays": 90, "decommissionDays": 180 },
  "byType":  { "phone": { "confirmedDays": 14, "probableDays": 45, "noRecentDays": 90, "decommissionDays": 180 } },
  "reliability": { "sccm": 0.75, "intune": 0.8, "xdr": 0.8, "netskope": 0.7, "mam": 0.55, "m365": 0.6, "entra": 0.4, "ad": 0.25 },
  "minConfirmedSources": 2
},
"governance": { "signInWindowDays": 14, "maxSignInPages": 40, "urlBlocklistLimit": 1000, "urlBlocklistReservePercent": 10 },
"collection": { "historyRetentionDays": 400 },
"web": { "authMode": "open" }       // "entra" depois de validar o login (ADR-0008)
```

Segredos nunca aparecem em log: tokens, senhas e connection strings são removidos da configuração exportada e os logs são filtrados de novo antes de aparecer na tela de operações.

## 3. Execução local (desenvolvimento e demonstração)

```bash
export NEXUS_DATA_DIR=$PWD/.nexus-data NEXUS_DB_PASSWORD=<senha-dev>
dotnet build AzulNexus.slnx
dotnet src/Nexus.Cli/bin/Debug/net10.0/nexusctl.dll configure --from <install.resolved.json>   # "demoMode": true
dotnet src/Nexus.Cli/bin/Debug/net10.0/nexusctl.dll migrate
dotnet run --project src/Nexus.Worker &
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Nexus.Web
```

O modo demonstração usa dados sintéticos (`Nexus.Simulation`) e nunca conecta a SCCM, Intune, Entra ID ou AD. Nenhuma tela de produção mostra dado simulado: sem `demoMode`, fonte sem coleta aparece como "aguardando coleta".

Verificações no navegador (site em demonstração, aberto no próprio servidor):

```bash
PLAYWRIGHT_MODULE=<caminho>/playwright/index.mjs node tests/ui/theme.mjs
PLAYWRIGHT_MODULE=<caminho>/playwright/index.mjs node tests/ui/flows.mjs
AXE=<caminho>/axe-core/axe.min.js PLAYWRIGHT_MODULE=<caminho>/playwright/index.mjs node tests/ui/audit.mjs
```

## 4. Publicação

```powershell
pwsh deploy/Publish-AzulNexus.ps1        # artifacts/AzulNexus-<versão>/ e .zip (self-contained, sem instalador, ADR-0001)
```

O workflow `Release` publica o pacote como GitHub Release a cada push na `main`. No servidor: descompactar e rodar `Instalar.cmd` (idempotente: backup do banco, cópia dos binários, migrações, concessões, serviços, verificação). Atualizar é rodar `Instalar.cmd` do pacote novo.

## 5. Proxy corporativo e operação sem internet

- Proxy: `nexusctl network-proxy --url http://proxy.azul.corp:8080` vale para o Graph e para o Netskope. Por padrão o proxy é autenticado com a conta do serviço; `--no-credentials` desliga isso e `--off` remove o proxy.
- Sem acesso direto à internet: o pacote é self-contained (não precisa de runtime nem de Hosting Bundle no modo serviço), o site usa fontes do sistema (nenhuma CDN) e as bibliotecas de teste de navegador não vão para produção. O servidor precisa alcançar apenas `login.microsoftonline.com` e `graph.microsoft.com` (pelo proxy) e o tenant do Netskope.
- Pacote offline: copiar o `.zip` do Release para o servidor; o instalador não baixa nada obrigatório (o Hosting Bundle só é necessário no modo IIS).

## 6. Rollback

1. Pare os serviços `AzulNexus.Web` e `AzulNexus.Worker`.
2. Restaure o backup do banco do Nexus feito pelo instalador (pasta `backups`, COPY_ONLY no SQL Server; `pg_dump` no PostgreSQL).
3. Reinstale o pacote anterior com `Instalar.cmd` (as migrações do pacote antigo não se aplicam sobre um banco mais novo: por isso o passo 2 vem primeiro).
4. Confirme com `nexusctl status`.

As migrações `Phase5` a `Phase11` só acrescentam colunas e tabelas, portanto o pacote anterior continua lendo o banco novo, mas ignora o que não conhece. Mesmo assim restaure o backup quando precisar voltar ao estado exato.

## 7. Verificação depois de instalar

1. `https://<servidor>:<porta>/healthz` responde `ok`.
2. **Operações › Fontes e coletas**: todas as fontes configuradas em dia; opcionais sem configuração não aparecem.
3. `nexusctl kpis` e **Visão executiva**: ativos únicos, estados e fila de atenção.
4. **Qualidade dos dados**: conflitos e lacunas conhecidas; investigue antes de confiar na contagem.
