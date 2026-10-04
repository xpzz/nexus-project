# Arquitetura do Azul Nexus

Inventário reconciliado do parque, no lugar de uma CMDB que não existe. Somente leitura nas fontes. Decisões em `docs/adr/`.

```text
                         ┌──────────────── servidor do SCCM ────────────────┐
 SCCM (SQL, views)  ───▶ │                                                   │
 AD (LDAP)          ───▶ │  Nexus.Worker  ── coletas ──▶ tabelas de origem   │
 Graph (certificado)───▶ │  (agendador,    (último estado)  + versões        │
 Cortex XDR (SQL)   ───▶ │   fila, retry,        │          + linha do tempo │
 Netskope (API)     ───▶ │   Run ID)             ▼                           │
                         │              Reconciler → Motor de evidências     │
                         │              (ativo único)  (estado, score, tipo) │
                         │                       │                           │
                         │   Banco do Nexus ◀────┘ (SQL Server ou PostgreSQL)│
                         │        ▲                                          │
                         │  Nexus.Web (Blazor, SSR) ── RBAC (Entra ID) ──▶ usuários
                         └───────────────────────────────────────────────────┘
```

## Projetos

| Projeto | Responsabilidade |
|---|---|
| `Nexus.Core` | Configuração (inclui `Evidence` e `Governance`), catálogo de erros, saúde, permissões do Graph. |
| `Nexus.Data` | EF Core (uma migração por provedor), fila de comandos, auditoria, versões de registros (`RawRecordArchive`). |
| `Nexus.Collectors.*` | Um leitor por fonte, independente e substituível: SCCM, AD, Graph (core e governança), XDR, Netskope. Fontes opcionais não impedem o núcleo. |
| `Nexus.Reconciliation` | Reconciliação (ADR-0004), motor de evidências (ADR-0007), modelo de saúde, relatórios (`ExecutiveSummary`, `MamReport`, `SourceComparison`), histórico (`AssetHistory`), consulta do inventário. |
| `Nexus.Worker` | Jobs, gate de CPU e pausas, execuções com Run ID, limpeza de histórico. |
| `Nexus.Web` | Interface (SSR estático; interativo só em assistente e saúde), RBAC, endpoints de filtros, exportação, coleta manual, exceções e auditoria. |
| `Nexus.Cli` | `nexusctl`: configuração, migrações, concessões, coleta, KPIs, backup, diagnóstico. |
| `Nexus.Simulation` | Dados sintéticos determinísticos, só para demonstração e testes. |

## Fluxo de uma coleta

1. O agendador (ou um comando manual) chama o job; ele recebe um **Run ID** que entra nos logs e nas versões.
2. A coleta lê a fonte com limites (lotes, timeout, `READ UNCOMMITTED` no SCCM, `$batch` de 20 no Graph com retry por sub-requisição e saneamento de identificadores).
3. A tabela de origem é substituída numa transação (falha mantém o último estado válido). Depois, o arquivo de versões grava só o que mudou.
4. O job de reconciliação roda em seguida: liga registros por chaves fortes (Entra Device ID, serial, UUID; nome e MAC só como apoio), aplica o motor de evidências, grava ativos, vínculos e fila de revisão, e registra mudanças e a linha do tempo.
5. A Web serve um instantâneo em memória (renovado a cada 60 s, ou na hora após uma ação na interface).

## Modelo de dados (resumo)

| Conceito | Tabelas |
|---|---|
| Registro bruto por fonte | `sccm_devices`, `ad_computers`, `intune_devices`, `entra_devices`, `entra_users`, `xdr_endpoints`, `netskope_clients`, `mam_registrations` + **`raw_record_versions`** (histórico) |
| Identidade e chaves de correlação | `asset_links` (fonte, chave, evidência, confiança, motivo); serial, UUID, MAC, FQDN e IPs no ativo |
| Ativo consolidado | `assets` (ID interno estável, tipo, estado, score, explicação, postura de gestão) |
| Evidência de atividade | `evidence_timeline` + sinais em `assets.EvidenceJson` |
| Resultado de reconciliação | `assets`, `review_items` |
| Cobertura MDM, MAM e conformidade | campos do ativo, `intune_policies`, `intune_device_policy_states`, `app_protection_policies`, `app_configs`, `conditional_access_policies`, `access_evidence` |
| Exceções | `protection_exceptions` |
| Histórico | `asset_changes`, `evidence_timeline`, `raw_record_versions` |
| Execução de coleta e erros | `job_runs`, `jobs` |
| Auditoria | `audit_events` |
| Filtros salvos | `saved_views` |

## Segurança

Somente leitura nas fontes (papel de banco com `SELECT`; permissões do Graph `*.Read.All`). Segredos fora do código: variáveis de ambiente, DPAPI e certificado. Logs sem segredos, filtrados de novo na tela. Papéis do Entra ID (ADR-0008). Ações administrativas auditadas. Dados pessoais de BYOD mascarados para Leitura; MAC de aparelho pessoal nunca é guardado; IMEI, telefone e localização nunca são lidos.
