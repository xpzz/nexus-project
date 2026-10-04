# Diagnóstico técnico do Azul Nexus (antes da reconstrução)

Auditoria feita no código da `main` em 03/10/2026 (commit `3d1e570`), antes de reconstruir o produto como inventário reconciliado de ativos. Cada achado diz o que foi reaproveitado, o que mudou e o que continua pendente.

## 1. O que existia

| Camada | Estado |
|---|---|
| Web (Blazor, .NET 10) | Painel por grupo, BYOD, inventário e dispositivo, com KPIs de cobertura. Leitura aberta sem login (ADR-0005). |
| Worker | Jobs de coleta com gate de CPU, pausas e fila de comandos Web → Worker. |
| Conectores | SCCM (SQL, três níveis de consulta), AD (LDAP), Graph (Intune, Entra, MAM, políticas, usuários), Cortex XDR (tabela SQL), Netskope (API). |
| Reconciliação | Ativo único por Entra Device ID, serial e UUID; nome só liga AD, XDR e Netskope (ADR-0004). |
| Banco | EF Core com migrações separadas para SQL Server e PostgreSQL. |
| Implantação | `dotnet publish` + scripts PowerShell (serviço do Windows ou IIS), `nexusctl`. |
| Testes | 228 testes xUnit + scripts PowerShell dos instaladores. |

## 2. Componentes reaproveitáveis (mantidos)

- **Reconciler** (`Nexus.Reconciliation/Reconciler.cs`): união por chaves fortes, nome nunca liga gerenciamento, casos ambíguos viram fila de revisão. Mantido; recebeu MAC (só como evidência de apoio), registros MAM sem usuário e a passagem final do motor de evidências.
- **Leitores**: `SqlSccmReader` (camadas com fallback 207/208/229), `GraphHttpClient` (`$batch`, retry por sub-requisição, 429), `HttpGraphReader`, XDR e Netskope. Mantidos e estendidos.
- **Configuração e segredos**: `SettingsStore`, DPAPI, exportação sem segredos, proxy corporativo (`NetworkSettings`). Mantidos.
- **Catálogo de erros** ("o que aconteceu, impacto, como resolver"). Mantido; novo erro de permissão ausente para leituras opcionais.
- **Scripts de implantação e Azure**: mantidos; as funções do aplicativo web foram trocadas (ver ADR-0008).
- **Modo simulado** (`Nexus.Simulation`): mantido só para demonstração e testes. Nenhuma tela de produção mostra dado simulado: o modo vem de `demoMode` na configuração.

## 3. Código obsoleto ou inseguro (e o que foi feito)

| Achado | Situação |
|---|---|
| **Tema perdido ao navegar.** A navegação interna do Blazor sincroniza os atributos do `<html>` com o HTML do servidor, que não tinha `data-theme`; o atributo era apagado enquanto o `localStorage` mantinha a escolha. Havia ainda três blocos de tokens duplicados no CSS e inicialização em script inline. | **Corrigido** (ADR-0010): `theme.js` único, cookie lido pelo servidor, observador de atributo, `light-dark()`. 14 verificações no navegador. |
| **Evidência bruta sobrescrita.** Cada coleta apagava e recriava a tabela de origem; o ativo era recriado a cada reconciliação. Não havia como explicar uma classificação passada. | **Corrigido** (ADR-0009): versões de registros, linha do tempo de datas, histórico de mudanças e execuções com Run ID. |
| **Atividade por janela única de 30 dias**, sem explicação, por pool. Fontes de identidade pesavam como telemetria na contagem de fontes. | **Substituído** pelo motor de evidências (ADR-0007): limiares por tipo, 7 estados, score explicável. |
| **Registros MAM sem usuário eram ignorados** (nem ativo, nem revisão). | **Corrigido**: viram aparelhos "MAM apenas" com item de revisão. |
| **`$batch` com identificadores nulos, vazios, GUID zero ou repetidos** derrubaria o lote inteiro (HTTP 400). | **Corrigido**: `GraphIds` e saneamento no cliente (`DroppedRequests`). |
| `POST /dispositivo/{id}/inventario` com antiforgery desabilitado. | **Pendente, risco baixo** (só enfileira leitura somente leitura, limitada a um pedido por dispositivo). Os endpoints novos usam antiforgery. |
| Funções do aplicativo web (`Nexus.Admin`, `Gestao`, `Operacao`, `Seguranca`) sem nenhum uso no código. | **Substituídas** por Leitura, Analista, Administrador de integração e Auditoria, aplicadas pelo código (ADR-0008). |
| Acesso aberto por padrão (`web.openAccess = true`), sem RBAC. | **Mantido como padrão até o login estar validado no tenant**; `Web.AuthMode = entra` liga RBAC. Exportação, filtros salvos, coleta manual, exceções e auditoria já exigem papel. |
| Inventário "sem gestão" e KPIs com percentuais sobre todos os computadores (inclusive MAM). | **Reconstruído**: cada KPI declara população, unidade e janela; MAM usa pessoas e instâncias de app. |

## 4. Lacunas funcionais encontradas

Sem tipo de ativo; sem estado de atividade explicável; sem MAC, IP e chassi; sem histórico; sem Acesso Condicional, sem sign-ins, sem controles das políticas de proteção de apps, sem listas de URL do Edge; sem exceções; sem exportação auditada; sem trilha de auditoria legível; sem filtros salvos; sem execução manual por conector com histórico. Todos tratados nesta reconstrução, exceto o que está em `docs/pendencias-fontes.md`.

## 5. Falhas do modelo de dados

1. Tabelas de origem como snapshot mutável (sem versões). → `RawRecordVersion`.
2. `Asset` desnormalizado e recriado: o ID interno só sobrevivia pelos vínculos anteriores. → mantido (continua estável enquanto algum vínculo permanecer); mudanças ficam em `AssetChange`.
3. Sem lista de identificadores. → o detalhe do ativo monta a tabela de identificadores a partir dos vínculos e dos campos (serial, UUID, MAC, FQDN, IPs, IDs por fonte).
4. Sem separação entre identidade, atividade e risco. → `Confidence`, `OperationalState`/`ActivityScore` e índice de saúde ficam separados em campos e telas.

## 6. Integrações existentes

SCCM (SQL, somente leitura, views concedidas por papel), AD (LDAP), Graph com certificado (Intune, Entra, MAM, políticas, usuários), Cortex XDR (tabela SQL do site), Netskope (API do tenant). Novas: Graph de governança (proteção de apps, configuração de apps, Acesso Condicional, sign-ins) e SCCM estendido (MAC, IP, chassi).

## 7. Pendências técnicas que continuam

- **Nada foi executado contra SCCM, Intune, Entra, XDR ou Netskope reais.** Os testes usam HTTP e SQL simulados; os endpoints novos do Graph seguem a documentação da Microsoft.
- Login com Entra ID (OIDC com certificado) nunca rodou contra um tenant.
- O CI roda os testes em SQLite; as migrações foram geradas e aplicadas localmente em PostgreSQL, não em SQL Server.
- Versões de registros arquivam os dispositivos e o MAM; estados de política por dispositivo não são arquivados (volume).
- Sign-ins não interativos dependem do tenant aceitar o filtro por tipo de evento; há fallback para interativos.
- Fontes não conectadas: ver `docs/pendencias-fontes.md`.

## 8. Plano de migração sem perda de dados

1. As migrações `Phase5` a `Phase11` só adicionam colunas e tabelas; nada é removido ou renomeado.
2. `Instalar.cmd` faz backup do banco do Nexus antes de migrar (`nexusctl backup`) e aplica as migrações.
3. Na primeira reconciliação depois da atualização, `OperationalState`, `AssetType`, `ActivityScore` e a explicação são preenchidos; a primeira linha do tempo de datas é semeada; o histórico de mudanças começa vazio (a primeira carga não registra mudanças).
4. Configuração antiga continua válida: `Collection.ActivityWindowDays` deixou de decidir e `Evidence.*` assume (padrão 7/30/90/180 dias).
5. **Rollback**: restaurar o backup do banco e reinstalar o pacote anterior (`docs/guias/configuracao-e-implantacao.md`).
