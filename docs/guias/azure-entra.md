# Azure: Entra ID e Intune (registros, certificados, consentimento e acessos)

Um único script, `Configurar-Azure.cmd`, faz no servidor e no Entra ID tudo o que a SPEC §4 pede para esta etapa. Ele é **idempotente**: pode rodar de novo, e só cria ou concede o que faltar.

> **Estado desta versão:** o script deixa o Entra ID e o servidor prontos e **valida** que as permissões funcionam. O Nexus ainda **não lê o Intune/Entra nem faz login por SSO**: esses módulos entram no incremento 0.6. O assistente do Nexus mostra a etapa como "Registros criados" assim que o `azure.json` existe.

## Quem roda e onde
- No **servidor do Nexus**, como administrador local (precisa instalar certificados e ajustar permissão de chave).
- Com a conta de **Administrador Global** do Entra ID (ou Administrador de Aplicativos + Administrador de Função Privilegiada) para o login.
- O servidor precisa alcançar `login.microsoftonline.com` e `graph.microsoft.com` na porta 443 (o instalador já verifica isso).

## Como rodar
1. Na pasta do pacote, dê duplo clique em **`Configurar-Azure.cmd`** (ele pede permissão de administrador).
   - Opções, se quiser: `-AdminUser ana@empresa.com` (quem será o primeiro `Nexus.AdminIntegracao`; padrão: quem fizer o login), `-SkipOptionalPermissions` (não pede `Organization.Read.All`), `-ValidateOnly` (só valida).
   - Sem o pacote por perto: `powershell -ExecutionPolicy Bypass -File "C:\Program Files\Azul Nexus\deploy\Install-AzulNexusAzure.ps1"` (como administrador).
2. O script mostra um **código de dispositivo**. Abra https://microsoft.com/devicelogin em qualquer navegador (pode ser no seu computador), digite o código, entre como Administrador Global e **aprove** as permissões do "Microsoft Graph Command Line Tools". O token fica só na memória.
3. Aguarde. No fim, o script valida com a identidade do aplicativo; o consentimento pode levar alguns minutos para propagar, então ele tenta até 6 vezes.

## O que ele cria
| Onde | O quê |
|---|---|
| Servidor (`LocalMachine\My`) | Certificados `CN=AzulNexus-Coletor` e `CN=AzulNexus-Web` (RSA 2048, **chave não exportável**, 2 anos). A conta do serviço recebe leitura da chave |
| Servidor | `<dados>\scripts\azure\nexus-coletor.cer` e `nexus-web.cer` (públicos) |
| Servidor | `<dados>\config\azure.json`: locatário, Client IDs, impressões digitais e funções. **Sem segredos** |
| Entra ID | **Azul Nexus – Coletor**: permissões de **aplicativo**, somente leitura (abaixo), com consentimento do administrador |
| Entra ID | **Azul Nexus – Web**: SSO. URIs `https://<nome>:<porta>/signin-oidc` e `/signout-oidc`, permissões delegadas `openid`, `profile` e `User.Read` com consentimento, funções **Nexus.Leitura, Nexus.Analista, Nexus.AdminIntegracao e Nexus.Auditoria** |
| Entra ID | **Atribuição obrigatória** ativada no aplicativo Web e o primeiro administrador atribuído a `Nexus.AdminIntegracao` |

**Permissões do Coletor** (todas `*.Read.All`, nenhuma de escrita): `DeviceManagementManagedDevices`, `Device`, `User`, `GroupMember`, `DeviceManagementConfiguration`, `DeviceManagementApps`, `DeviceManagementServiceConfig` e, opcional, `Organization`.

Não há **segredo de cliente**: os dois aplicativos se autenticam por certificado. O registro opcional "Azul Nexus – Relatórios" (exportJobs, exige permissão de escrita) **não** é criado: pertence à Fase 4.

## Como conferir
- Entra ID › Registros de aplicativo: os dois "Azul Nexus – …" com o certificado carregado e as permissões "Concedido para <locatário>".
- Entra ID › Aplicativos empresariais › Azul Nexus – Web › Usuários e grupos: o administrador em `Nexus.AdminIntegracao`; Propriedades: *Atribuição obrigatória* = Sim.
- No servidor: `azure.json` e o log `<dados>\logs\azure-*.log`. Para revalidar depois: `Configurar-Azure.cmd -ValidateOnly`.
- No Nexus, o assistente mostra "3. Azure" como **Registros criados** e "4. Acesso e perfis" como **Funções criadas**.

## Códigos de saída
`0` sucesso · `10` bloqueio (nada foi criado) · `20` falha · `30` registros criados, mas a validação ainda tem falhas (aguarde alguns minutos e rode com `-ValidateOnly`).

## Problemas comuns
| Sintoma | Causa provável | O que fazer |
|---|---|---|
| Login falha ou nega o aplicativo "Microsoft Graph Command Line Tools" | A empresa bloqueia esse aplicativo ou exige Acesso Condicional | Peça ao time de identidade para liberar o aplicativo ou uma exceção para o seu login |
| `Authorization_RequestDenied` ao criar | A conta não é Administrador Global | Entre com a conta certa |
| Validação com falha logo depois de criar | O consentimento ainda não propagou | Aguarde 5–10 minutos e rode `-ValidateOnly` |
| `Chamada: DeviceManagement…` com **Atenção** | O locatário não tem Intune/licença para esse recurso | Esperado sem licença; o indicador aparecerá como "não habilitado" |
| Falha de token com `AADSTS700027` | Certificado não reconhecido pelo registro | Rode o script de novo (ele reenvia o certificado) |
| `AADSTS53003` | Acesso Condicional bloqueia a identidade do Nexus | Peça exceção para o aplicativo "Azul Nexus – Coletor" |

## Renovar o certificado
Faltando menos de 30 dias, rode o script de novo: ele gera um certificado novo, **acrescenta** a chave no registro (o antigo continua valendo) e revalida. Retire a chave antiga no Entra ID depois de confirmar que tudo funciona.

## Desfazer
O script não remove nada sozinho. Para desfazer: apague os registros "Azul Nexus – Coletor" e "Azul Nexus – Web" no Entra ID, remova os certificados `CN=AzulNexus-*` de `LocalMachine\My` e apague `<dados>\config\azure.json`.
