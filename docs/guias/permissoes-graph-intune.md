# Permissões do Microsoft Graph e do Intune

O Nexus lê o Microsoft Graph como o aplicativo **Azul Nexus – Coletor**, com certificado (nunca com a conta de serviço do SCCM e nunca com segredo). Todas as permissões são de **aplicativo**, somente leitura (`*.Read.All`). O script `Configurar-Azure.cmd` cria o aplicativo, o certificado e o consentimento; `-ValidateOnly` confere tudo sem alterar.

## Permissões

| Permissão | Para que serve | Obrigatória | Sem ela |
|---|---|---|---|
| `DeviceManagementManagedDevices.Read.All` | Dispositivos do Intune: sincronização, conformidade, propriedade, tipo de inscrição, MAC dos corporativos | sim | Sem Intune |
| `Device.Read.All` | Dispositivos do Entra: `deviceId`, associação e último login | sim | Sem Entra |
| `User.Read.All` | Usuários, área (AD) e conta habilitada | sim | Sem área nem usuário |
| `GroupMember.Read.All` | Nomes dos grupos nas atribuições | sim | Mostra o ID do grupo |
| `DeviceManagementConfiguration.Read.All` | Políticas de conformidade e perfis de configuração e seus estados por dispositivo | sim | Sem políticas por dispositivo |
| `DeviceManagementApps.Read.All` | Registros de proteção de apps (MAM), políticas de proteção iOS e Android com seus controles, configuração de apps (listas de URL do Edge) | sim | Sem MAM e sem controles |
| `DeviceManagementServiceConfig.Read.All` | Inscrição, Autopilot, APNs, ADE e VPP | sim | Sem dados de inscrição |
| `Organization.Read.All` | Licenças contratadas (marca KPIs como indisponíveis quando falta licença) | opcional | KPIs sem essa marcação |
| `Policy.Read.All` | **Acesso Condicional**: quais políticas existem e o que exigem | opcional | "Aguardando coleta" no lugar do Acesso Condicional |
| `AuditLog.Read.All` | **Sign-ins**: último acesso ao Microsoft 365 por dispositivo (Exchange, SharePoint, Teams, OneDrive) | opcional | "Funcionalidade não disponível" nos acessos |

`Policy.Read.All` e `AuditLog.Read.All` exigem **Microsoft Entra ID P1** (ou P2). Sem a licença, o Graph responde 403 e o Nexus mostra a permissão e a licença necessárias; o restante do inventário não é afetado.

## Como conceder

1. No servidor, como administrador: duplo clique em `Configurar-Azure.cmd` e entre com o Administrador Global (código de dispositivo). O script concede o consentimento às dez permissões (as três opcionais podem ser puladas com `-SkipOptionalPermissions`).
2. Para só conferir: `Configurar-Azure.cmd -ValidateOnly`. A validação usa o token do aplicativo, lê a claim `roles` e faz uma chamada real por permissão.
3. Depois, em **Saúde do Nexus**, a verificação "Azure: Entra ID e Intune" informa as opcionais sem consentimento sem tratá-las como falha.
4. Peça uma coleta em **Operações › Fontes e coletas** (ou `nexusctl collect intune`, `nexusctl collect entra`).

## O que cada coleta lê

| Job | Chamadas principais |
|---|---|
| `intune.devices` | `/deviceManagement/managedDevices` (campos mínimos; nunca IMEI, telefone nem localização) |
| `entra.devices`, `entra.users` | `/devices`, `/users/{id}` em `$batch` (identificadores nulos, vazios, GUID zero e repetidos são descartados antes) |
| `intune.policies` | Políticas de conformidade e configuração e `deviceCompliancePolicyStates` e `deviceConfigurationStates` por dispositivo |
| `intune.mam` | `/deviceAppManagement/managedAppRegistrations` com políticas aplicadas e operações (limpeza seletiva) |
| `intune.apppolicies` | `iosManagedAppProtections` e `androidManagedAppProtections` (com `/apps` e `/assignments`), `targetedManagedAppConfigurations` e `mobileAppConfigurations` |
| `entra.ca` | `/identity/conditionalAccess/policies` |
| `entra.signins` | `/auditLogs/signIns` da janela configurada (`Governance.SignInWindowDays`, padrão 14), resumidos por dispositivo; no máximo `Governance.MaxSignInPages` páginas de 500 por execução |

## Limites que o Nexus declara na tela

- **MAM protege só os aplicativos compatíveis com o Intune SDK** e o contexto corporativo. Não protege o aparelho nem o navegador pessoal.
- **Android pessoal com perfil de trabalho** é inferido (aparelho pessoal com MDM); o Graph não rotula o perfil de trabalho.
- **Listas de URL do Edge** governam só o Edge corporativo (aplicativo e perfil gerenciados). A lista de bloqueio tem limite operacional de 1.000 entradas; o Nexus avisa a partir de 900 (`Governance.UrlBlocklistLimit` e `UrlBlocklistReservePercent`).
- **Acesso permitido pelo Acesso Condicional** é o resultado do último sign-in de cada dispositivo (sucesso, falha ou não aplicado). Não simula o que a política permitiria.
- Sign-ins sem dispositivo identificado aparecem como "acesso móvel sem aparelho identificado", não como ativo.

## Papéis do aplicativo web (login)

No aplicativo **Azul Nexus – Web**, atribua os usuários ou grupos (grupos exigem Entra ID P1) a `Nexus.Leitura`, `Nexus.Analista`, `Nexus.AdminIntegracao` e `Nexus.Auditoria`. Depois de validar o login, defina `Web.AuthMode = entra` em `nexus.json`. Ver ADR-0008.
