# Fase 0 — Proposta de plano (aguardando aprovação)

Escopo: SPEC seção 12, Fase 0 — pacote, instalação e configuração. Nenhum código será escrito antes da aprovação deste plano.

## 1. Estrutura da solução

Segue a estrutura do SPEC 5.5, com três acréscimos (marcados com ★):

```text
nexus/
├─ CLAUDE.md
├─ Directory.Build.props / Directory.Packages.props   ★ versões centralizadas, nullable, warnings como erro
├─ global.json                                        ★ fixa o SDK .NET 10
├─ THIRD-PARTY-NOTICES.md
├─ docs/  SPEC.md, plans/, adr/, guias/
├─ src/
│  ├─ Nexus.Core/                 domínio, contratos (IConnector, IHealthCheck, IClock), catálogo de erros
│  ├─ Nexus.Platform/             ★ abstrações do SO (serviços, ACL, certificados, registro, firewall,
│  │                                 Job Object, DPAPI, WMI) + implementação Windows
│  ├─ Nexus.Data/                 EF Core 10, DbContext único, migrações separadas por provedor
│  ├─ Nexus.Collectors.Sccm/       leitor das views (só SELECT, READ UNCOMMITTED, lotes, timeout)
│  ├─ Nexus.Collectors.Graph/      Fase 0: só token + validação de permissões; coleta real na Fase 1
│  ├─ Nexus.Collectors.ActiveDirectory/  IDirectoryReader + LDAP (S.DS.Protocols) + fake em memória
│  ├─ Nexus.Collectors.Optional/   só a interface e o estado "não configurado"
│  ├─ Nexus.Reconciliation/        esqueleto (Fase 1)
│  ├─ Nexus.Worker/                agendador, fila de comandos, limites de CPU/concorrência, janelas
│  ├─ Nexus.Web/                   Blazor Server + Fluent UI, modo de configuração, assistente, Azure A/B/C, saúde
│  ├─ Nexus.Setup.Engine/          etapas idempotentes (Check/Apply/Rollback), arquivo de respostas
│  ├─ Nexus.Setup.Wizard/          WPF, 10 telas, só chama o Engine
│  └─ Nexus.Cli/                   nexusctl (System.CommandLine)
├─ installer/   WiX: MSI só com arquivos, serviços, registro, firewall e origem do Log de Eventos
├─ simulators/
│  ├─ Nexus.Simulators.DataGen/    gerador sintético determinístico (semente) com casos de borda
│  ├─ sccm-db/                     scripts T-SQL que criam as views do SPEC 6.1 em SQL Server de container
│  └─ Nexus.Simulators.Graph/      WireMock.Net (token, managedDevices, devices, users, 429, 403, roles parciais)
└─ tests/  Unit, Integration (Testcontainers), Setup.Engine, Windows.E2E
```

## 2. Decisões técnicas (cada uma vira ADR em `docs/adr`)

| # | Decisão | Motivo |
|---|---|---|
| ADR-001 | O MSI só instala arquivos, serviços (desabilitados até configurar), registro, firewall e origem de eventos. **Toda** a configuração (pastas/ACL, identidades, certificados, banco, grants, HTTPS, health check) fica no `Nexus.Setup.Engine`, chamado pelo Wizard, pelo `/quiet` e pelo `nexusctl repair`. | Premissa 6: um motor só. Evita custom actions DTF (licença MS-RL distribuída) e lógica difícil de testar dentro do MSI. |
| ADR-002 | Cada etapa implementa `Detect → Plan → Apply → Verify → Rollback`; `Apply` sobre estado já correto é no-op. O Engine produz um "plano" serializável antes de executar. | Idempotência testável (rodar 2× = mesmo estado) e "Resumo" da tela 8 = plano. |
| ADR-003 | `Setup.exe` é um executável .NET self-contained próprio (WPF) que embute o MSI, em vez de bundle WiX Burn. | Mesmo motor no gráfico e no silencioso; códigos de saída 0/3010/10/20 controlados por nós; menos dependência do WiX. |
| ADR-004 | Agendador próprio e enxuto sobre tabelas EF (`jobs`, `job_runs`, `commands`) com lease/lock no banco, em vez de Quartz.NET. | O SPEC aceita "ou equivalente"; evita manter dois esquemas Quartz (PostgreSQL e SQL Server) fora das migrações EF; a tabela de comandos Web→Worker já é exigida. |
| ADR-005 | Testes de acesso sempre executados **pelo Worker** (comando `RunHealthChecks` na tabela de comandos); Setup e Web só leem o resultado. | Premissa 5. |
| ADR-006 | Segredos locais (senha do PostgreSQL embarcado, chaves do Data Protection) protegidos por DPAPI de máquina **e** arquivo com ACL só para a conta do serviço; certificados do Entra com chave CNG não exportável e ACL por serviço. Configuração em JSON sem segredos. | Premissa 9 e critério "varredura sem segredos". |
| ADR-007 | Contas virtuais `NT SERVICE\AzulNexus.Web` / `.Worker` / `.Database`; gMSA quando o SQL acessado é remoto (o Engine detecta e bloqueia conta virtual contra SQL remoto). | SPEC 5.2. |
| ADR-008 | Modo simulador é um modo oficial (`"simulators"` no arquivo de respostas, oculto na UI): aponta SCCM para o SQL de teste, Graph para o WireMock e AD para o fake. O modo demonstração usa o mesmo gerador. | Permite cumprir o aceite em VM limpa sem ambientes reais. |
| ADR-009 | Migrações EF em dois assemblies (`Nexus.Data.Migrations.Postgres` / `.SqlServer`), ambos testados via Testcontainers no CI. Backup antes de migrar: `pg_dump` (embarcado) ou `BACKUP DATABASE`/script DBA (externo). | SPEC 5.4 e 3.6. |
| ADR-010 | UI: Fluent UI Blazor (MIT); gráficos com ApexCharts (MIT) via wrapper Blazor MIT. Textos de UI em pt-BR em arquivos de recursos; catálogo de erros com `{oQueAconteceu, impacto, comoResolver, script?}`. | SPEC 5.1 e 13. |
| ADR-011 | Worker se coloca num Job Object com limite de CPU (hard cap) e memória, prioridade BelowNormal, semáforo de concorrência SQL (padrão 2) e "recuo" quando CPU do servidor ou duração de consulta passam do limite. | Premissa 8 / SPEC 5.3. |

## 3. Entregas incrementais da Fase 0

| Incremento | Conteúdo | Verificável em Linux? |
|---|---|---|
| 0.1 Fundação | Solução, build central, CLAUDE.md, CI (Linux + Windows), THIRD-PARTY-NOTICES gerado, varredura de segredos no CI | Sim |
| 0.2 Dados | DbContext, migrações nos 2 provedores, tabelas de config/auditoria/jobs/comandos/saúde | Sim (Testcontainers) |
| 0.3 Simuladores | Gerador sintético, views SCCM no SQL Server de container, WireMock do Graph, fake do AD | Sim |
| 0.4 Worker | Agendador, comandos, coleta SCCM/AD "de fumaça" (contagens), limites, janelas de pausa, health checks | Lógica sim; Job Object só Windows |
| 0.5 Web | Modo de configuração (localhost/código de uso único 24 h), assistente checklist, Saúde, diagnóstico, export/import de config | Sim |
| 0.6 Azure | Caminhos A/B/C gerados da mesma lista de permissões, arquivo de retorno, validação (OpenID, token por certificado, `roles`, chamada mínima por permissão), erros AADSTS traduzidos, SSO, renovação de certificado | Sim, contra o mock |
| 0.7 Engine + CLI | Verificações 3.3, etapas 3.4, arquivo de respostas, reparo, upgrade com backup/rollback, `nexusctl` (status, test, collect, pause, backup, export/import, setup-code, recover-access) | Lógica com fakes; real só Windows |
| 0.8 Instalador | Wizard WPF, MSI WiX, Setup.exe, assinatura (cert. de teste até o pipeline Venafi), E2E em Windows | Não — exige Windows |

## 4. Riscos

1. **Ambiente de desenvolvimento.** Este container é Linux, sem SDK .NET e sem Windows. WPF, MSI, serviços Windows, ACLs, repositório de certificados, contas virtuais, Job Objects e WMI só podem ser construídos/testados em Windows. Mitigação: `Nexus.Platform` com fakes para testes em Linux; job de CI `windows-latest` para build do Wizard/MSI e testes de plataforma; **VM Windows Server limpa** (com snapshot) para o critério de aceite de 10 minutos — precisa ser fornecida.
2. **Licença do WiX.** WiX v4+ é MS-RL e, desde a v6, há a Open Source Maintenance Fee para uso comercial. Mitigação: o MSI não distribui binários do WiX (ADR-001/003); a confirmação jurídica continua pendente (SPEC 14).
3. **PostgreSQL embarcado.** Os binários Windows usuais (EDB) incluem bibliotecas LGPL (ex.: libintl/libiconv), o que conflita com a premissa 14. Mitigação proposta: compilar PostgreSQL para Windows sem NLS no nosso pipeline (só PostgreSQL License + OpenSSL Apache 2.0 + zlib) ou validar cada DLL do pacote; tamanho do instalador cresce (~40–60 MB).
4. **Instância SQL do SCCM.** Detectar "instância licenciada com o ConfigMgr" é heurístico (mesma instância que hospeda `CM_xxx`). Mitigação: bloqueio conservador + confirmação explícita.
5. **Mock de autenticação.** Azure.Identity exige authority HTTPS; o mock precisa de TLS com certificado confiável no teste. Mitigação: WireMock com certificado gerado no setup do teste.
6. **Caminho A** depende do módulo Microsoft Graph PowerShell, que não será distribuído; sem ele, a tela recomenda o caminho B (conforme SPEC).
7. **Tamanho e prazo.** A Fase 0 é a maior fase (instalador completo antes de indicadores). Proponho validar com você ao fim de cada incremento.
8. **Assinatura de código.** Sem acesso ao Venafi no desenvolvimento; o pipeline terá a etapa de assinatura parametrizada e usará certificado de teste.

## 5. Plano de testes

- **Unidade (xUnit):** cada etapa do Engine com fakes de plataforma — Apply em estado limpo, Apply 2× (idempotência), Rollback, falha no meio; arquivo de respostas (round-trip: salvar no Wizard → `/quiet` → configuração idêntica); catálogo de erros (toda mensagem tem as 3 partes); modo de configuração (código expira, uso único, encerra com SSO + Admin); janelas de pausa e recuo.
- **Integração (Testcontainers):** migrações nos dois provedores (vazio → atual, e versão N-1 → N com backup); coletor SCCM contra as views simuladas com login **com** e **sem** o papel de leitura (critério "teste falha sem permissão do serviço"); concorrência máxima de consultas.
- **Azure contra o mock:** para cada permissão do SPEC 4.5, remover uma a uma e verificar que a validação aponta exatamente a faltante; 429 com Retry-After; paginação; erros AADSTS → mensagem traduzida; caminhos A/B/C geram a mesma configuração (comparação de JSON normalizado).
- **Segredos:** varredura automatizada (padrões de senha, chave privada, connection string com senha, tokens JWT) em pasta de dados, logs e pacote de diagnóstico após um ciclo completo.
- **Windows E2E (CI `windows-latest` + VM):** instalação silenciosa, health check verde, primeira coleta, reexecução = reparo, upgrade N-1 → N, desinstalação preservando dados, limite de CPU do Job Object.

## 6. Pontos que preciso de você para aprovar

1. Aprovação geral da estrutura e dos ADRs 001–011 (em especial ADR-003 Setup.exe próprio e ADR-004 agendador próprio no lugar do Quartz).
2. Disponibilidade de runners Windows no CI e de uma VM Windows Server limpa para o aceite.
3. Caminho para o PostgreSQL embarcado (risco 3): compilar sem NLS ou aceitar revisão DLL a DLL.
4. Começo pelos incrementos 0.1–0.3, com relatório ao fim de cada um?
