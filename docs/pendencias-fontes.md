# O que depende de fontes ainda não conectadas

Lista objetiva. Nada abaixo é simulado em produção: a tela mostra "aguardando coleta", "sem evidência" ou "funcionalidade não disponível".

## Dados e fontes

| Item | Depende de | Situação |
|---|---|---|
| Acesso permitido por Acesso Condicional (por dispositivo) | Job `entra.signins` e `entra.ca`, permissões `AuditLog.Read.All` e `Policy.Read.All`, Entra ID P1 | Implementado; precisa do consentimento no tenant. Sem ele a tela diz "não disponível". |
| Acesso ao Microsoft 365 por dispositivo | `entra.signins` | Idem. Só sign-ins com Entra Device ID entram como atividade do ativo. |
| Sign-ins não interativos | Tenant aceitar o filtro por tipo de evento | Tenta; cai para os interativos se o tenant recusar. |
| Defender para Endpoint | Conector próprio (API do Defender) | **Não implementado.** O agente de segurança hoje é o Cortex XDR. |
| Qualys (vulnerabilidades e último scan) | Conector próprio | **Não implementado.** |
| Sinais de rede (DHCP, switches, NAC) | Fonte ainda não escolhida | **Não implementado.** |
| Patch e avaliação do cliente SCCM | `v_UpdateComplianceStatus`, `v_CH_EvalResults` | Views já concedidas; leitura ainda não implementada. KPI "Patch em até 30 dias" aparece como aguardando coleta. |
| Sistema abaixo do mínimo, PIN do aparelho | Intune (compliance detalhada por regra) | Ainda não lidos por regra. |
| macOS corporativo via ADE | `DeviceManagementServiceConfig.Read.All` (concedida) | Dados de inscrição ainda não lidos; o Mac aparece como dispositivo do Intune. |
| Android com perfil de trabalho (rótulo) | O Graph não rotula | Inferido: pessoal e com MDM. Declarado na tela. |
| Limpeza seletiva: evento por registro | `managedAppRegistrations` com `operations` | Lido quando o tenant expõe; sem o dado a tela diz que não há operação registrada ou que o tenant não expõe. |
| Servidor sem Intune nem MAM | n/a | Esperado: a gestão esperada é SCCM. |
| Tipo notebook/desktop | Chassi do SCCM (`v_GS_SYSTEM_ENCLOSURE`) | Se a view não puder ser lida, vale o modelo e o prefixo de nome (`Evidence.TypeNameTokens`); sem nenhum, "tipo não identificado". |

## Validação

| Item | Situação |
|---|---|
| SCCM, AD, Intune, Entra, XDR, Netskope reais | **Nunca executado.** Os testes usam SQL e HTTP simulados. |
| Login com Entra ID (OIDC com certificado) | Nunca executado contra um tenant. |
| Endpoints novos do Graph (políticas de proteção, configurações de apps, Acesso Condicional, sign-ins, operações do MAM) | Seguem a documentação da Microsoft; testados com respostas simuladas. Validar em homologação. |
| Migrações em SQL Server | Geradas e testadas no PostgreSQL local e em SQLite (CI). Aplicar em homologação antes de produção. |
| Escala | Testado com ~700 ativos sintéticos. Sem medição com dezenas de milhares. |

## Como fechar cada item

1. Conceda `Policy.Read.All` e `AuditLog.Read.All` (guia de permissões) e peça uma coleta.
2. Rode a instalação em um servidor de homologação com o SCCM real e confira **Operações** e **Qualidade dos dados** primeiro: dados incompletos aparecem ali antes de aparecerem nos números.
3. Valide o login com um usuário de teste em cada papel antes de definir `Web.AuthMode = entra`.
