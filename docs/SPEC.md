# Azul Nexus — Especificação de Desenvolvimento (v2)

3 de out. de 2026 · @Juliano

## 0. Como usar com o Claude Code

Exporte este documento em Markdown, salve no repositório nexus como `docs/SPEC.md` e abra o Claude Code com o prompt abaixo.

```text
Leia docs/SPEC.md por completo. Este é o projeto Azul Nexus.
Trabalhe por fases, começando pela Fase 0 (pacote, instalação e configuração).
Antes de escrever código, proponha a estrutura da solução, as decisões técnicas,
os riscos e o plano de testes da fase, e aguarde minha aprovação.
As premissas da seção 2 prevalecem sobre qualquer outra parte do documento.
Nunca conecte a SCCM, Intune, Entra ID ou AD reais durante o desenvolvimento:
use os simuladores da seção 13.
Ao final de cada fase, entregue um relatório comparando o que foi feito
com os critérios de aceite da seção 12.
```

Pontos ainda não confirmados com a Azul estão na seção 14, cada um com o padrão adotado até a confirmação.

## 1. Objetivo

O Azul Nexus consolida SCCM, Intune, Active Directory e Microsoft Entra ID em um inventário único, para medir com evidências a cobertura de gerenciamento e a postura dos dispositivos.

Por padrão, é instalado no servidor do SCCM, lê as views do banco do Configuration Manager e mantém base própria para cruzamentos, histórico e indicadores. Deve responder:

1. Quais dispositivos deveriam estar gerenciados e estão fora da gestão?
2. Quais estão somente no SCCM, somente no Intune ou nos dois?
3. Quais têm registro nas ferramentas, mas deixaram de se comunicar?
4. Quais Windows pessoais usam Intune MDM ou proteção MAM?
5. Como estão os celulares: enrollments, políticas e conformidade?
6. Quais problemas exigem atuação, por equipe, departamento e criticidade?

AD e Entra ID são fontes complementares: um equipamento ausente do SCCM e do Intune só é conhecido se aparecer em outra fonte. A cobertura é sempre apresentada sobre o universo conhecido, com as fontes usadas. Recursos que dependem de licença não confirmada aparecem como “não habilitado” quando indisponíveis.

A versão 1 é de visibilidade, diagnóstico e acompanhamento; não executa ações de correção nos dispositivos.

## 2. Premissas do pacote (não negociáveis)

Instalar e configurar o Nexus deve ser avançar, avançar, concluir: sem pré-requisitos manuais, sem risco ao SCCM e com cada configuração testável. Em caso de conflito, estas premissas prevalecem sobre o restante do documento.

1. **Um pacote, avançar-avançar-concluir.** Um instalador assinado com padrões inteligentes: quem só clica em “Avançar” termina com a solução funcionando. Nada a instalar antes: sem runtime .NET, IIS, módulos PowerShell ou banco de dados.
2. **Pronto ao concluir.** Ao clicar em “Concluir”: serviços em execução, banco criado, HTTPS ativo, SCCM e AD detectados e testados, primeira coleta em andamento e navegador aberto no assistente. Só o registro de aplicativos no Entra ID fica para o assistente, porque pode depender de outra pessoa.
3. **Detectar antes de perguntar.** Site, SQL e banco do SCCM, domínio, controladores, FQDN, certificado HTTPS, proxy e conectividade são detectados. O usuário só confirma ou corrige.
4. **Todo campo tem “Testar”; todo erro tem solução.** Mensagens dizem o que aconteceu, o impacto e como corrigir, em português, com script ou comando pronto quando houver.
5. **Testar com a identidade que vai operar.** Testes de acesso rodam no serviço, com a conta do serviço. A conta de quem instala costuma ter mais privilégios e mascara problemas.
6. **Silencioso e reproduzível.** Tudo que o assistente faz também roda sem interface, por arquivo de respostas e pelo mesmo motor. A configuração, sem segredos, pode ser exportada e importada entre ambientes.
7. **Idempotente, reparável e atualizável.** Rodar o instalador de novo repara. Atualizações preservam configuração e dados e fazem backup antes de migrar o banco. A desinstalação preserva dados por padrão.
8. **Não prejudicar o SCCM.** Somente leitura nas views, nenhuma alteração de esquema, porta própria e sem IIS. Limites de CPU, memória e concorrência, com pausa automática sob carga e em janelas de manutenção.
9. **Segredos protegidos e menor privilégio.** Autenticação no Entra por certificado, não por segredo. Nada sensível em texto claro em disco, log ou diagnóstico. Interface e coleta com identidades separadas.
10. **Separação de funções.** Administrador do SCCM, administrador do Entra e DBA podem ser pessoas diferentes. Para cada um, o Nexus gera script e instruções exatas e depois valida o resultado.
11. **Degradação elegante.** Módulo sem configuração, sem licença ou com falha aparece como “não configurado”, “não habilitado” ou “desatualizado”, sem derrubar o resto nem distorcer indicadores.
12. **Suporte em um clique.** Página de saúde, logs estruturados e “Gerar pacote de diagnóstico”, com logs, versões, configuração sem segredos e resultado de todos os testes.
13. **Instalável fora do servidor do SCCM.** O mesmo pacote funciona em servidor dedicado, com detecção remota ou informação manual do site, sem mudança de código.
14. **Dependências limpas e código assinado.** O que é distribuído com o produto usa só licenças permissivas (MIT, Apache 2.0, BSD, PostgreSQL), listadas em THIRD-PARTY-NOTICES. Binários, MSI e instalador são assinados com o certificado corporativo de code signing (ex.: via Venafi).

## 3. Experiência de instalação

Um assistente gráfico de dez telas instala e deixa a solução pronta só com os padrões; o mesmo motor executa a instalação silenciosa, o reparo e a atualização.

### 3.1 Artefatos do pacote

| Artefato | Função |
|---|---|
| `AzulNexus-Setup-X.Y.Z.exe` | Assistente gráfico e modo silencioso. Contém o MSI e o motor de configuração |
| `AzulNexus-X.Y.Z.msi` | Arquivos, serviços e registro no Windows Installer (upgrade, reparo, desinstalação) |
| `nexusctl.exe` | CLI: status, testes, coleta, pausa, backup, exportação de configuração e recuperação de acesso |
| `answers.sample.json` | Modelo do arquivo de respostas |
| `docs/` | Guias de instalação, do administrador do Entra, do DBA e de operação |

### 3.2 Telas do instalador

| # | Tela | O que faz | Padrão |
|---|---|---|---|
| 1 | Boas-vindas | Versão, o que será instalado, requisitos e opção de modo demonstração (dados sintéticos) | Instalação normal |
| 2 | Verificação do ambiente | Roda as checagens de 3.3; cada item fica OK, Atenção ou Bloqueio, com “como resolver” | Avança se não houver bloqueio |
| 3 | Local e acesso | Pasta do programa, pasta de dados, porta HTTPS e nome de acesso | Program Files; dados no volume com mais espaço fora do SO; porta 8443; FQDN do servidor |
| 4 | Banco do Nexus | Embarcado (PostgreSQL local gerenciado pelo Nexus) ou externo (SQL Server ou PostgreSQL corporativo); testa, cria o banco ou gera script para o DBA | Embarcado |
| 5 | Identidade dos serviços | Conta virtual, gMSA ou conta de domínio; concede “Fazer logon como serviço” | Conta virtual quando o SQL acessado é local; gMSA quando é remoto |
| 6 | Leitura do SCCM | Mostra o site detectado; concede SELECT nas views (se quem instala puder) ou gera script T-SQL para o DBA | Conceder agora, se possível |
| 7 | Certificado HTTPS | Escolhe o melhor certificado da máquina, solicita à AC corporativa ou gera autoassinado (só piloto, com aviso) | Melhor certificado encontrado |
| 8 | Resumo | Mostra as escolhas; “Salvar arquivo de respostas”; “Instalar” | — |
| 9 | Instalação | Progresso por etapa (3.4), log visível e rollback se algo falhar | — |
| 10 | Concluído | Endereço, código de configuração inicial e “Abrir o assistente de configuração” | Marcado |

### 3.3 Verificações automáticas

- **Servidor:** Windows Server x64 suportado pelo .NET 10, execução como administrador, membro do domínio, CPU, RAM e disco mínimos.
- **Porta:** livre e sem conflito com portas do SCCM e do WSUS (ex.: 443, 8530, 8531).
- **SCCM:** código, tipo (CAS, primário ou secundário) e versão do site; servidor e banco SQL, pelo registro do site e pelo WMI do SMS Provider (`SMS_ProviderLocation`, `SMS_SCI_SiteDefinition`). Em CAS ou secundário, orientar a escolha do banco correto.
- **SQL do site:** conectividade, local ou remoto, e TLS. O Microsoft.Data.SqlClient criptografa por padrão; se o certificado do SQL não for confiável, explicar e oferecer `TrustServerCertificate` com aviso.
- **Active Directory:** domínio, controlador via DNS, LDAP assinado/selado ou LDAPS e catálogo global para florestas com vários domínios.
- **Internet:** proxy WinHTTP da máquina e TLS 1.2+ para `login.microsoftonline.com`, `graph.microsoft.com` e o armazenamento de blobs usado no download dos relatórios exportados do Intune.
- **Políticas locais:** se “Fazer logon como serviço” vem de GPO, a concessão local seria sobrescrita: indicar o que incluir na GPO. Alertar sobre AppLocker ou WDAC que possa bloquear os binários.

### 3.4 Etapas executadas

Cada etapa é idempotente, registrada em log e desfeita no rollback.

1. Instala o MSI: binários self-contained, serviços, regra de firewall, origem no Log de Eventos e chave `HKLM\SOFTWARE\Azul\Nexus`.
2. Cria pastas de configuração, logs, dados e backups com ACL restrita a Administradores e às contas dos serviços.
3. Configura as identidades dos serviços Web e Worker.
4. Gera os certificados de cliente do Entra (Web e Coletor) em `LocalMachine\My`, com chave não exportável legível só pelo respectivo serviço, e exporta os `.cer` públicos.
5. Provisiona o banco: inicializa o PostgreSQL embarcado (só 127.0.0.1, senha aleatória protegida, serviço próprio) ou conecta ao externo, e aplica as migrações.
6. Concede leitura nas views do SCCM ou registra a pendência com o script do DBA.
7. Vincula o certificado HTTPS e concede acesso à chave privada ao serviço Web.
8. Inicia os serviços e executa o health check pelo próprio serviço (premissa 5).
9. Dispara a primeira coleta do SCCM e do AD.
10. Gera o código de configuração inicial e abre o assistente.

### 3.5 Instalação silenciosa

```text
AzulNexus-Setup.exe /quiet /answers "C:\temp\nexus-answers.json" /log "C:\temp\nexus-install.log"
```

```json
{
  "installDir": "auto",
  "dataDir": "auto",
  "https": { "port": 8443, "hostName": "auto", "certificate": "auto" },
  "database": { "mode": "embedded" },
  "serviceIdentity": { "mode": "auto", "gmsa": null },
  "sccm": { "site": "auto", "grantViewAccess": "if-permitted" },
  "activeDirectory": { "domain": "auto", "searchBases": [] },
  "entra": { "configureLater": true },
  "demoMode": false
}
```

Códigos de saída: `0` sucesso, `3010` reinício necessário, `10` bloqueio de pré-requisito e `20` falha com rollback concluído. Para implantar pelo próprio SCCM, a detecção usa o código de produto do MSI ou a versão em `HKLM\SOFTWARE\Azul\Nexus`.

### 3.6 Atualização, reparo e desinstalação

- **Atualização:** o upgrade do MSI preserva configuração, certificados e dados. Faz backup antes das migrações; se a migração falhar, restaura o backup e mantém a versão anterior.
- **Reparo:** reexecutar o Setup ou “Reparar” em Programas e Recursos recria serviços, ACLs, firewall e vínculos sem perder dados.
- **Desinstalação:** remove serviços, binários e firewall e preserva dados e backups, salvo escolha explícita. Gera scripts para remover o login SQL e os registros no Entra; nada fora do servidor é removido sozinho.

### 3.7 O que fica no servidor

```text
C:\Program Files\Azul Nexus\   binários (somente leitura para os serviços)
<dados>\AzulNexus\config\       configuração, sem segredos em texto claro
<dados>\AzulNexus\logs\         logs rotativos em JSON
<dados>\AzulNexus\db\           PostgreSQL embarcado (se usado)
<dados>\AzulNexus\backups\      backups do banco e da configuração
```

Serviços: `AzulNexus.Web`, `AzulNexus.Worker` e `AzulNexus.Database` (só no modo embarcado).

## 4. Assistente de configuração e tela do Azure

Após a instalação, um assistente dentro da aplicação conclui a configuração; a tela do Azure (Entra ID e Intune) tem três caminhos que chegam à mesma configuração e à mesma validação.

### 4.1 Primeiro acesso seguro

Antes do SSO existir, o Nexus fica em modo de configuração: acessível no próprio servidor (localhost) ou remotamente com o código exibido ao fim da instalação. O código é de uso único e expira em 24 horas; `nexusctl setup-code` gera outro, apenas para administradores locais.

O modo de configuração termina quando o SSO é validado e há ao menos um usuário em `Nexus.AdminIntegracao`. Se o acesso se perder, `nexusctl recover-access` reabre o modo de configuração e registra o evento na auditoria.

### 4.2 Etapas do assistente

O assistente é uma lista de verificação: cada etapa mostra Concluída, Pendente, Com erro ou Opcional e pode ser refeita depois em Configurações.

| Etapa | Conteúdo | Situação logo após instalar |
|---|---|---|
| 1. SCCM | Site, SQL, views acessíveis, limites e janelas de pausa | Concluída (detectada) |
| 2. Active Directory | Domínios, bases de busca (OUs) e atributos coletados | Concluída (detectada) |
| 3. Azure: Entra ID e Intune | Registros de aplicativo, permissões, consentimento e validação (4.3 a 4.8) | Pendente |
| 4. Acesso e perfis | SSO, funções, grupos e escopos por departamento | Pendente |
| 5. Gerenciamento esperado | Modelos por grupo de dispositivos (8.1), ajustáveis | Modelos padrão aplicados |
| 6. Coleta | Frequências, limites de CPU, memória e concorrência e janelas de pausa | Padrões aplicados |
| 7. Fontes opcionais | CMDB, Cortex/XDR, Defender e Log Analytics | Opcional |
| 8. Revisão | Roda todos os testes e mostra os primeiros números por fonte e a sobreposição entre elas | — |

### 4.3 Tela do Azure: três caminhos

Antes de começar, a tela mostra as funções exigidas. Criar os registros exige Administrador de Aplicativos ou Administrador de Aplicativos de Nuvem. Consentir permissões de aplicativo do Microsoft Graph exige Administrador de Função Privilegiada ou Administrador Global.

| Caminho | Quando usar | Como funciona |
|---|---|---|
| A. Automático | Quem está no servidor tem as funções no Entra | Executa ali o mesmo script do caminho B, com login interativo do administrador via Microsoft Graph PowerShell. O token não é armazenado. Sem o módulo no servidor, sugere o caminho B |
| B. Enviar ao administrador do Entra | Funções separadas, o cenário mais provável | Gera um pacote com script PowerShell idempotente, certificados públicos (.cer), instruções com os valores deste ambiente e um arquivo de retorno. O administrador executa e devolve o arquivo, ou informa Tenant ID e Client IDs |
| C. Manual pelo portal | Política que impede scripts | Passo a passo na tela (4.6), com cada valor pronto para copiar |

### 4.4 Registros de aplicativo

| Registro | Uso | Credencial | Permissões |
|---|---|---|---|
| Azul Nexus – Web | Login SSO e perfis | Certificado do serviço Web | Delegadas: openid, profile e User.Read. Funções de aplicativo: Nexus.Leitura, Nexus.Analista, Nexus.AdminIntegracao e Nexus.Auditoria (ADR-0008) |
| Azul Nexus – Coletor | Leitura de Intune e Entra | Certificado do serviço Worker | De aplicativo, somente leitura (4.5) |
| Azul Nexus – Relatórios (opcional) | exportJobs do Intune | Certificado próprio | As exigidas pela criação de exportJobs (a referência lista ReadWrite), só para os relatórios habilitados |

Segredo de cliente existe apenas como alternativa explícita, desativada por padrão.

### 4.5 Permissões do Coletor

O Nexus pede só as permissões dos módulos ativos; script, pacote e passo a passo são gerados a partir desta lista.

| Permissão de aplicativo | Para quê | Fase |
|---|---|---|
| DeviceManagementManagedDevices.Read.All | Dispositivos gerenciados, sincronização e compliance por dispositivo | 1 |
| Device.Read.All | Dispositivos do Entra: deviceId, trustType e atividade | 1 |
| User.Read.All | Usuários, departamento e conta habilitada | 1 |
| GroupMember.Read.All | Grupos usados em escopos e no gerenciamento esperado | 1 |
| DeviceManagementConfiguration.Read.All | Políticas de compliance e configuração e seus estados | 2 |
| DeviceManagementApps.Read.All | Aplicativos e MAM (políticas e registros de proteção) | 2 |
| DeviceManagementServiceConfig.Read.All | Enrollment, Autopilot, APNs, ADE e VPP | 2 |
| Organization.Read.All (opcional) | Licenças contratadas, para marcar a disponibilidade dos KPIs; validar a de menor privilégio | 2 |

### 4.6 Passo a passo manual (conteúdo da tela)

Cada passo mostra o nome do menu em português e em inglês, o valor a copiar e o resultado esperado.

1. Entre em entra.microsoft.com com uma conta que tenha as funções de 4.3.
2. Registros de aplicativo › Novo registro: nome “Azul Nexus – Coletor”, contas apenas deste diretório organizacional, sem URI de redirecionamento.
3. Copie o ID do aplicativo (cliente) e o ID do diretório (locatário) para o Nexus. O Nexus valida o formato e confirma o locatário pelos metadados OpenID públicos.
4. Certificados e segredos › Certificados › Carregar certificado: envie o `nexus-coletor.cer` baixado no Nexus e confira se a impressão digital é a mesma exibida no Nexus.
5. Permissões de API › Adicionar uma permissão › Microsoft Graph › Permissões de aplicativo: marque as permissões listadas pelo Nexus.
6. Clique em “Conceder consentimento do administrador” e confirme que todas ficaram concedidas.
7. Crie “Azul Nexus – Web” do mesmo modo, com URI de redirecionamento Web `https://{fqdn}:{porta}/signin-oidc` e logout `https://{fqdn}:{porta}/signout-oidc`, como exibidos no Nexus. Envie `nexus-web.cer`, adicione as permissões delegadas e conceda consentimento.
8. Funções de aplicativo › Criar função de aplicativo: crie as quatro funções com os valores exibidos, para Usuários/Grupos.
9. Aplicativos empresariais › Azul Nexus – Web › Propriedades: ative a atribuição obrigatória. Em Usuários e grupos, atribua ao menos um administrador a Nexus.AdminIntegracao; atribuição por grupo exige Entra ID P1.
10. Volte ao Nexus e clique em “Validar”.

### 4.7 Validação

“Validar” roda item a item, também uma vez por dia:

- Locatário existente, pelos metadados OpenID públicos.
- Token obtido com o certificado de cada registro.
- Permissões concedidas, lidas da declaração `roles` do token e comparadas com as exigidas pelos módulos ativos, apontando cada uma que falta.
- Uma chamada funcional mínima por permissão (ex.: primeiro registro de managedDevices, devices e users).
- Recursos do ambiente: Intune ativo, co-management, tenant attach, tratamento de dispositivos sem política de compliance (`deviceManagement/settings`, `secureByDefault`) e licenças, se permitido.
- SSO: login de teste do próprio administrador, função presente no token e atribuição obrigatória ativa.
- Certificados: alerta 30 dias antes do vencimento e botão “Renovar certificado”, que gera novo par, orienta o upload e só retira o antigo depois de validar o novo.

### 4.8 Erros traduzidos

Amostra inicial; o catálogo cresce conforme novos erros aparecem.

| Erro | Mensagem ao usuário | Ação sugerida |
|---|---|---|
| AADSTS700016 | Aplicativo não encontrado neste locatário | Conferir Client ID e Tenant ID |
| AADSTS700027 | Certificado não reconhecido pelo registro | Reenviar o .cer e conferir a impressão digital |
| AADSTS90002 | Locatário não encontrado | Conferir o Tenant ID |
| AADSTS53003 | Bloqueado por Acesso Condicional | Pedir exceção ou política adequada para a identidade do Nexus |
| AADSTS50105 | Usuário sem atribuição no Azul Nexus | Atribuir o usuário ou grupo a uma função |
| Graph 403 | Permissão ausente ou sem consentimento | Mostrar a permissão faltante e regenerar o pacote do caminho B |

### 4.9 Configurações, saúde e diagnóstico

- **Configurações:** as mesmas etapas do assistente, cada uma com “Testar”.
- **Saúde:** estado de cada serviço e coletor (última execução, duração, registros, erros, próxima execução); CPU, memória e disco do servidor; duração das consultas ao SQL do SCCM; validade de certificados e tokens; espaço do banco.
- **Operação:** Coletar agora, Pausar/Retomar coletores, Reparar e Gerar pacote de diagnóstico.
- **Portabilidade:** exportar e importar a configuração sem segredos, para replicar homologação em produção.
- **Monitoramento:** eventos críticos também no Log de Eventos do Windows, para as ferramentas de monitoramento existentes.

## 5. Arquitetura técnica

Arquitetura nativa de Windows: dois serviços .NET 10 separados por privilégio, sem IIS, com banco próprio fora do banco do SCCM.

O SQL do site pode estar no próprio servidor ou em servidor dedicado; com o Nexus fora do servidor do SCCM (premissa 13), o desenho é o mesmo.

### 5.1 Stack

| Camada | Escolha | Motivo |
|---|---|---|
| Runtime | .NET 10 LTS, publicado self-contained (win-x64) | Nada a instalar no servidor; o .NET 8 sai de suporte em 10/11/2026 |
| Web | ASP.NET Core (Kestrel) como serviço Windows; Blazor Interactive Server, Fluent UI Blazor e biblioteca de gráficos MIT | Sem IIS, visual alinhado aos portais Microsoft e uma linguagem só |
| Dados | EF Core 10 com PostgreSQL (embarcado ou externo) e SQL Server (externo) | Migrações automáticas nos dois provedores |
| Microsoft Graph | Microsoft Graph SDK e Azure.Identity com certificado | Sem segredos, com retentativas e paginação |
| Active Directory | System.DirectoryServices.Protocols, LDAP assinado/selado ou LDAPS, paginado | Leitura mínima de atributos |
| Agendamento | Quartz.NET com persistência no banco, ou equivalente | Execuções persistidas, sem sobreposição, com retomada e pausa |
| Logs | Serilog em JSON rotativo e Log de Eventos para eventos críticos | Suporte e monitoramento |
| Instalador | MSI (WiX), assistente WPF e nexusctl sobre um motor compartilhado | O mesmo motor serve o gráfico, o silencioso e o Reparar |
| Testes | xUnit, Testcontainers (PostgreSQL e SQL Server) e WireMock.Net | Simuladores da seção 13 |

### 5.2 Processos e identidades

| Componente | Identidade | Acessa | Não acessa |
|---|---|---|---|
| AzulNexus.Web: HTTPS em porta própria, interface, API e SSO | Conta virtual; gMSA se o banco do Nexus for externo | Banco do Nexus e certificado Web | SCCM, AD e Graph |
| AzulNexus.Worker: agendador, coletores e processador | Conta virtual; gMSA se o SQL do site ou o banco do Nexus forem remotos | Views do SCCM e AD (leitura), certificado do Coletor e banco do Nexus | Interface e portas de entrada |
| AzulNexus.Database (só modo embarcado) | Conta virtual | Pasta de dados; escuta só em 127.0.0.1 | Rede externa |

Conta virtual aparece na rede como a conta de computador do servidor, que no SCCM costuma ser sysadmin no SQL do site. Por isso, todo acesso a SQL remoto usa gMSA. Web e Worker conversam por uma tabela de comandos no banco (ex.: coletar agora, pausar), sem portas extras.

### 5.3 Proteção do servidor do SCCM

- Worker com prioridade reduzida e limites de CPU e memória por Job Object, configuráveis.
- Consultas ao SQL do site só com as colunas necessárias, timeout, no máximo 2 simultâneas por padrão e em lotes por ResourceID.
- Isolamento que não bloqueia a escrita do site (READ UNCOMMITTED), registrado como trade-off.
- Recuo automático: se a CPU do servidor ou a duração das consultas passar dos limites, a coleta é adiada.
- Janelas de pausa configuráveis (ex.: backup do site, manutenção do SQL) e parada do Nexus independente do SCCM.
- Consultas dos usuários sempre atendidas pelo banco do Nexus, nunca pelo banco do SCCM.
- A Microsoft recomenda servidores dedicados para funções de site com IIS. O impacto será medido em teste de carga; a premissa 13 permite mover o Nexus sem mudar código.

### 5.4 Banco do Nexus

- **Separado do banco do SCCM.** A instância SQL licenciada com o Configuration Manager não pode hospedar outros bancos; o instalador detecta essa instância e bloqueia, salvo confirmação explícita de licenciamento próprio.
- **Padrão:** PostgreSQL embarcado e gerenciado pelo Nexus, com serviço próprio, backup diário com retenção, atualização junto com o produto e dependências de runtime incluídas no pacote.
- **Produção corporativa:** SQL Server licenciado ou PostgreSQL corporativo, com criação automática (se houver permissão) ou script para o DBA.
- Migrações do EF Core para os dois provedores, testadas automaticamente nos dois.

### 5.5 Estrutura do repositório

```text
nexus/
├─ CLAUDE.md                      comandos e convenções para o Claude Code
├─ docs/                          SPEC.md, ADRs e guias (instalação, Entra, DBA, operação)
├─ src/
│  ├─ Nexus.Core/                 domínio: ativos, estados, regras e KPIs
│  ├─ Nexus.Data/                 EF Core e migrações (PostgreSQL e SQL Server)
│  ├─ Nexus.Collectors.Sccm/
│  ├─ Nexus.Collectors.Graph/
│  ├─ Nexus.Collectors.ActiveDirectory/
│  ├─ Nexus.Collectors.Optional/  CMDB, XDR, Defender e Log Analytics
│  ├─ Nexus.Reconciliation/
│  ├─ Nexus.Worker/               serviço: agendador, coletores e processador
│  ├─ Nexus.Web/                  serviço: Blazor, API, SSO e assistente
│  ├─ Nexus.Setup.Engine/         etapas idempotentes de instalação e configuração
│  ├─ Nexus.Setup.Wizard/         instalador gráfico (WPF)
│  └─ Nexus.Cli/                  nexusctl
├─ installer/                     WiX (MSI) e empacotamento do Setup
├─ simulators/                    banco SCCM simulado, mock do Graph e gerador de dados
└─ tests/                         unidade, integração e ponta a ponta do instalador
```

## 6. Integrações

Cada fonte tem coletor próprio, somente leitura e com seleção mínima de campos; nenhuma consulta de usuário vai direto às fontes.

### 6.1 SCCM (views SQL)

Views iniciais; validar a disponibilidade e as classes de inventário habilitadas em cada site.

| View | Uso |
|---|---|
| v_R_System | Recursos descobertos, cliente, ativo/obsoleto, AADDeviceID e SMBIOS GUID |
| v_CH_ClientSummary | Comunicação, último inventário, política, management point e avaliação de saúde |
| v_CH_EvalResults | Resultado das verificações de saúde do cliente |
| v_GS_COMPUTER_SYSTEM | Fabricante e modelo |
| v_GS_PC_BIOS, v_GS_SYSTEM_ENCLOSURE | Serial, BIOS e tipo de chassi |
| v_GS_COMPUTER_SYSTEM_PRODUCT | UUID de hardware |
| v_GS_OPERATING_SYSTEM | Sistema operacional, versão, build e último boot |
| v_FullCollectionMembership | Associação às coleções |
| v_R_User, v_UsersPrimaryMachines | Usuários e afinidade usuário-dispositivo |
| v_ClientCoManagementState | Estado de co-management e workloads |
| v_UpdateComplianceStatus | Conformidade de atualizações (Fase 2) |

- A conta lê só as views usadas, por um papel de banco dedicado com SELECT explícito. O script é gerado pelo Nexus, revisável pelo DBA e reversível.
- A leitura SQL direta não passa pelo controle de acesso do SMS Provider; os escopos de visibilidade são implementados pelo Nexus.

### 6.2 Microsoft Graph (Intune e Entra)

- managedDevices com `$select` e paginação; devices, users e grupos por consultas delta (incrementais).
- Presença em managedDevices não prova enrollment MDM. Tenant attach aparece com agente do Configuration Manager sem MDM, e o gerenciamento de segurança do Defender aparece com agente próprio (ex.: msSense). O estado vem de `managementAgent`, `deviceEnrollmentType` e demais atributos.
- `azureADDeviceId` do Intune corresponde ao `deviceId` do Entra, não ao `id` do objeto do diretório.
- Relatórios em massa via exportJobs, com controle de expiração dos arquivos exportados.
- Tratar paginação, 429 com Retry-After, retentativas com backoff e mudanças de esquema. Priorizar v1.0; dependências de /beta ficam isoladas e sinalizadas na página de saúde.
- Endpoint Analytics, Advanced Analytics e relatórios do Windows Update for Business (via Log Analytics) têm pré-requisitos próprios, verificados antes de o KPI ser marcado como disponível.
- Inventário Windows detalhado (Properties Catalog) e inventário aprimorado de aplicativos dependem de configuração própria e são validados nos dispositivos elegíveis.

### 6.3 Active Directory

- **Computadores:** name, dNSHostName, operatingSystem, operatingSystemVersion, lastLogonTimestamp, pwdLastSet, whenCreated, userAccountControl, distinguishedName, objectGUID, objectSid, managedBy e description.
- **Usuários:** sAMAccountName, userPrincipalName, displayName, department, company, title, manager, mail e userAccountControl.
- lastLogonTimestamp tem defasagem de até cerca de 14 dias; pwdLastSet do computador, trocado a cada 30 dias por padrão, complementa. Existir no AD não comprova atividade.
- Consultas paginadas, assinadas/seladas ou via LDAPS, só com os atributos listados.

### 6.4 Fontes opcionais

| Fonte | Papel |
|---|---|
| CMDB | Patrimônio, criticidade, responsável, localização e ciclo de vida |
| Cortex ou outro XDR | Evidência de atividade e cobertura de proteção |
| Defender | Risco, vulnerabilidades e recomendações do serviço contratado |
| Log Analytics | Atualizações e outros serviços habilitados |

Todas usam uma interface de conector comum, ficam desativadas por padrão e aparecem como “não configurado”. Vulnerabilidade só vem dessas fontes: software no inventário, sozinho, não comprova vulnerabilidade.

## 7. Modelo de dados e reconciliação

Cada dispositivo recebe um ID interno do Nexus, preserva os registros originais de cada fonte e tem estados independentes de identidade, gerenciamento e proteção.

Um computador pode estar no Entra ID sem enrollment no Intune, ou aparecer no Intune por tenant attach sem MDM; esses estados nunca são fundidos em um só.

### 7.1 Dimensões do ativo

| Dimensão | Informações |
|---|---|
| Identificação | Nome atual e anteriores, fabricante, modelo, serial e identificadores disponíveis |
| Identidade | Objeto no AD, objeto no Entra e tipo de associação ao Entra |
| Gerenciamento | Registro SCCM, cliente, saúde do cliente, enrollment Intune e canais adicionais |
| Propriedade | Corporativo, pessoal ou desconhecido, com a origem da classificação |
| Responsabilidade | Usuário principal, usuários associados, departamento e responsável |
| Plataforma | Windows cliente, Windows Server, Android, iOS/iPadOS, macOS e outras no escopo |
| Postura | Compliance, configurações, criptografia, atualizações e agentes obrigatórios |
| Atualidade | Última comunicação, inventário, avaliação e coleta, por fonte |
| Governança | Criticidade, gerenciamento esperado, exceções, prazo de regularização e histórico |

### 7.2 Correlação, em ordem de confiança

1. Identificadores compartilhados e verificáveis: deviceId do Entra, azureADDeviceId do Intune e AADDeviceID do SCCM.
2. Serial válido com fabricante e modelo, descartando seriais inválidos conhecidos (ex.: “To be filled by O.E.M.”, “Default string”, “System Serial Number”, zeros).
3. UUID de hardware, quando confiável; descartar nulos, repetidos e clones de VM.
4. Nome, domínio e evidências adicionais, apenas como apoio.

### 7.3 Regras de reconciliação

- Nome e usuário nunca unem registros sozinhos. A lógica trata renomeação, reinstalação, novo enrollment, troca de dono, VMs e seriais inválidos.
- Cada vínculo tem evidência, confiança e justificativa. Casos ambíguos vão para uma fila de validação (mesclar ou separar pela interface, com auditoria) e nunca contam como um.
- Departamento vem do usuário identificado no SCCM ou Intune, cruzado com AD e Entra. Equipamentos compartilhados têm área responsável definida, sem herdar o último usuário.
- Propriedade preserva o valor declarado na fonte e aponta divergências com patrimônio ou CMDB. Tipo de associação ao Entra, sozinho, não define propriedade.
- Registros MAM têm como unidade usuário + aplicativo + instância do dispositivo e não somam ao total de máquinas físicas sem identificação confiável.
- Cada evidência guarda três horários: quando o dispositivo reportou, quando a fonte disponibilizou e quando o Nexus coletou.

## 8. Classificação, regras e pendências

“Sem SCCM” e “sem Intune” são condições observadas; só viram pendência quando contrariam o gerenciamento esperado do grupo do dispositivo.

### 8.1 Gerenciamento esperado

Grupos são definidos por critérios: plataforma, OU, grupo do Entra, coleção do SCCM, propriedade e criticidade. O produto vem com modelos ajustáveis no assistente.

| Modelo de grupo | Gerenciamento esperado (padrão, a confirmar) |
|---|---|
| Windows corporativo | SCCM saudável + Intune MDM (co-management) |
| Windows Server | SCCM + EDR; Intune MDM não exigido |
| Windows pessoal | Intune MDM ou MAM |
| Celular corporativo | Intune MDM |
| Celular pessoal | MAM |

### 8.2 Situações observadas

| Situação observada | Interpretação |
|---|---|
| Windows corporativo com SCCM saudável e Intune MDM | Gestão conjunta; validar os workloads de co-management |
| Windows corporativo com SCCM, sem Intune | Pendência se o grupo deve ter Intune |
| Windows corporativo com Intune, sem SCCM | Regular se o grupo é gerido só pelo Intune |
| Descoberto pelo SCCM, sem cliente | Conhecido, mas não gerenciado pelo cliente |
| Conhecido no AD, Entra ou CMDB, ausente dos gerenciadores | Possível lacuna, conforme atividade e elegibilidade |
| Windows pessoal inscrito no Intune | BYOD com MDM |
| Windows pessoal com apps protegidos por MAM | BYOD com proteção de aplicativos, sem MDM |
| Android ou iPhone pessoal com MAM | Proteção de apps avaliada separadamente da compliance MDM |
| Presente nas ferramentas, sem comunicação recente | Gerenciamento desatualizado ou interrompido |
| Windows Server sem Intune MDM | Avaliar pela política de servidores, não pela dos desktops |

Para servidores, distinguir gerenciamento SCCM, proteção por EDR e gerenciamento de configurações de segurança via Defender, que aplica políticas sem enrollment MDM completo.

### 8.3 BYOD

| Categoria | Avaliação |
|---|---|
| BYOD com MDM | Enrollment, sincronização, compliance e configurações |
| BYOD com MAM | Apps protegidos, políticas, check-in e sinais de saúde |
| BYOD com MDM e MAM | Dispositivo e aplicativos avaliados separadamente |
| Acesso pessoal sem proteção comprovada | Evidência de acesso com proteção insuficiente ou desconhecida |

Windows MAM protege o acesso corporativo em equipamentos pessoais, inclusive com Microsoft Edge, sem gerenciar o dispositivo. Seus requisitos e sinais são distintos do MDM.

### 8.4 Celulares e Apple

O catálogo distingue Android Enterprise (perfil de trabalho pessoal, perfil corporativo, totalmente gerenciado, dedicado) e, na Apple, ADE, enrollment manual, User Enrollment, supervisão e afinidade de usuário. Acompanha:

- Dispositivos preparados para enrollment e ainda não inscritos.
- Falhas de enrollment: códigos, recorrência e usuários afetados.
- Reenrollments e registros antigos do mesmo equipamento.
- Dispositivos sem sincronização ou com usuário desabilitado.
- Sistema abaixo da versão mínima da organização.
- Root ou jailbreak, quando houver sinal válido.
- Aplicativos obrigatórios ausentes ou com falha de instalação.
- Certificado APNs e tokens ADE e VPP: validade e última sincronização.

### 8.5 Regras de pendência

| Regra | Resultado |
|---|---|
| Windows corporativo ativo deveria ter SCCM, sem cliente confirmado | Instalar ou investigar o cliente |
| Grupo exige Intune, sem enrollment confirmado | Pendência de enrollment |
| Compliant sem política obrigatória | Lacuna de atribuição de controles |
| Cliente SCCM sem comunicação, com atividade recente no Intune | Investigar comunicação ou saúde do cliente |
| Celular com política exigida falhando | Pendência com controle, evidência e prazo |
| Token Apple perto do vencimento | Renovação com responsável |
| Equipamento com usuário desabilitado | Validar responsável, devolução ou baixa |
| Aplicativo obrigatório ausente | Pendência de deployment, conforme evidência |

A prioridade considera criticidade, atividade recente, controles afetados, idade da pendência e impacto operacional. Exceções têm justificativa, responsável, escopo e validade. A integração com ServiceNow (Fase 3) vincula chamados, com deduplicação por ativo e regra.

## 9. Catálogo de KPIs

Os KPIs formam um catálogo extensível: cada um declara fórmula, fonte e população elegível, e nenhum é exibido como disponível se o ambiente não o suporta.

Cada KPI tem definição, fórmula, fonte, população elegível, frequência, versão e estado: disponível, não habilitado, não aplicável, sem permissão ou sem dados. Desconhecidos, exceções e dados vencidos aparecem sempre separados.

### 9.1 Famílias

| Família | Indicadores |
|---|---|
| Inventário e cobertura | Ativos únicos; só SCCM; só Intune; ambos; nenhum; descobertos sem cliente; cobertura por plataforma, propriedade, área e criticidade |
| Saúde SCCM | Clientes ativos e inativos; versões; avaliação saudável ou com falha; inventário e política atrasados; remediações; comunicação por site e management point |
| Saúde Intune | Última sincronização; sem contato por faixa de tempo; novos registros; canais; propriedade desconhecida; duplicados ou antigos |
| Co-management | Elegibilidade; enrollment concluído; falhas; workloads por autoridade; divergência entre esperado e reportado |
| Enrollment | Inscrições por período, plataforma e método; falhas por código; tempo até concluir; reenrollments; preparados e não inscritos |
| Compliance | Compliant, noncompliant, carência, erro, conflito e desconhecido; sem política; controles que mais falham; reincidência; tempo de regularização |
| Configuração | Sucesso, erro, conflito, pendente e não aplicável; falhas por configuração; cobertura das configurações obrigatórias |
| Atualizações | Cobertura dos patches exigidos; pendentes; atraso em relação à baseline; falhas de scan, instalação e deployment; reinício pendente; versões do Windows |
| Aplicativos | Inventário e versões; obrigatórios instalados; falhas por app e código; fora da baseline; não aprovados |
| Segurança do endpoint | Criptografia; TPM e Secure Boot; firewall; agentes obrigatórios; estado do Defender quando usado |
| BYOD e MAM | Windows e celulares pessoais; usuários e apps protegidos; políticas; check-in; versões inadequadas; limpeza seletiva |
| Hardware e ciclo de vida | Modelos, RAM, armazenamento, espaço livre, BIOS, idade patrimonial e candidatos à troca |
| Experiência do endpoint | Inicialização, confiabilidade de apps e desempenho por modelo, com Endpoint Analytics habilitado |
| Recursos avançados | Anomalias e bateria; Endpoint Privilege Management; Remote Help; catálogo empresarial de apps, conforme contratação e API |
| Identidade e organização | Sem responsável; usuário desabilitado; departamento desconhecido; vários dispositivos por usuário; divergências entre fontes |
| Governança operacional | Pendências por responsável; idade; SLA; exceções vigentes e vencidas; reincidência; evolução da cobertura |
| Qualidade dos dados | Atualidade por fonte; falhas de coleta; registros sem identificador; correlações ambíguas; campos ausentes; telemetria por KPI |

### 9.2 Denominadores explícitos

| KPI | Cálculo |
|---|---|
| Cobertura SCCM | Elegíveis com cliente confirmado ÷ dispositivos que devem usar SCCM |
| Cobertura SCCM saudável | Elegíveis com cliente saudável e evidência recente ÷ dispositivos que devem usar SCCM |
| Cobertura Intune MDM | Elegíveis com enrollment confirmado ÷ dispositivos que devem usar MDM |
| Cobertura completa | Dispositivos com todos os canais exigidos ÷ elegíveis |
| Compliance reportada | Reportados como compliant ÷ população MDM selecionada, com os demais estados detalhados |
| Aderência validada | Controles obrigatórios atendidos com evidência recente ÷ elegíveis |
| Cobertura MAM | Usuários ou instâncias protegidas ÷ população MAM identificável |
| Patching no SLA | Baseline de atualização atendida ÷ elegíveis àquela baseline |

O tenant pode considerar compliant um dispositivo sem política atribuída. O Nexus identifica essa condição e mantém separados o estado reportado pela Microsoft e a aderência aos controles obrigatórios da Azul.

## 10. Coleta e atualidade

A coleta é periódica e nunca apresentada como tempo real; uma falha preserva o último resultado válido, marcado como desatualizado, sem derrubar artificialmente a cobertura.

| Conjunto | Frequência inicial |
|---|---|
| Identificação e status SCCM | 30 min |
| Dispositivos e sincronização Intune | 30 a 60 min |
| Compliance, configurações e deployments | 1 a 4 h, conforme volume e relatório |
| AD, Entra e departamentos | 4 a 6 h, com delta onde houver |
| Inventário detalhado de aplicações e hardware | Diário, respeitando a atualização da origem |
| Certificados e tokens | Diário |
| Consolidação histórica dos KPIs | Diário |

As frequências são ajustáveis na etapa Coleta do assistente. Consultar o banco a cada 30 minutos não torna recente um inventário antigo: a atualidade é medida pelo horário em que o dispositivo reportou, não pelo da coleta.

## 11. Segurança, acesso e privacidade

Acesso só por SSO do Entra, com quatro perfis e escopos aplicados pelo próprio Nexus, dados pessoais mínimos e auditoria de tudo que muda.

- Perfis por função de aplicativo: Administração, Gestão, Operação e Segurança. Escopos por departamento ou grupo são aplicados pelo Nexus, já que a leitura SQL não herda o RBAC do SCCM.
- Dados pessoais e identificadores sensíveis só para os perfis que precisam deles; mascarados nos demais.
- LGPD: em dispositivos pessoais, coletar só o necessário aos indicadores definidos (ex.: não guardar telefone ou IMEI sem finalidade).
- Auditoria de acessos, exportações, configuração, regras, exceções e mesclagens manuais.
- Versão 1 sem ações sobre dispositivos. Ações futuras ficam em módulos próprios, com identidade, autorização por função e registro da execução.
- Retenção inicial: evidências operacionais por 90 a 180 dias e indicadores diários por 13 meses. Mudanças de departamento, responsável e gerenciamento mantêm histórico.

## 12. Fases e critérios de aceite

A entrega tem cinco fases verificáveis; a Fase 0 entrega o pacote instalável e configurável antes de qualquer indicador.

### Fase 0 — Pacote, instalação e configuração

Solução base, serviços Web e Worker, banco e migrações, instalador gráfico e silencioso, assistente, tela do Azure completa, SSO, saúde, diagnóstico, nexusctl, simuladores, modo demonstração, build e assinatura.

- [ ] Em VM limpa com Windows Server e simuladores, instalar só com “Avançar” em até 10 minutos, sem pré-requisito manual, terminando com serviços ativos, health check verde e primeira coleta SCCM/AD concluída.
- [ ] Instalação silenciosa com o arquivo de respostas salvo produz configuração idêntica.
- [ ] Reexecução repara sem perda de dados; atualização entre duas versões migra o banco com backup automático; desinstalação preserva os dados.
- [ ] Caminhos A, B e C do Azure resultam na mesma configuração, e a validação aponta cada permissão faltante (testado no mock com permissões removidas uma a uma).
- [ ] Varredura automatizada não encontra segredos em disco, logs ou pacote de diagnóstico.
- [ ] Teste de acesso falha quando a conta do serviço não tem permissão, mesmo que quem instalou tenha.
- [ ] Worker respeita os limites de CPU e concorrência e pausa nas janelas configuradas.
- [ ] Modo de configuração encerra após o SSO validado; `nexusctl recover-access` o reabre com registro em auditoria.

### Fase 1 — Inventário reconciliado e cobertura

SCCM, Intune, AD e Entra; classificação de cobertura; ausentes, duplicados e dados antigos; fila de correlações ambíguas.

- [ ] Totais reproduzíveis nas fontes.
- [ ] Amostras conhecidas corretamente correlacionadas: renomeação, reimagem, VM clonada, serial inválido, tenant attach sem MDM e BYOD com MAM.
- [ ] Falha de uma integração não altera indevidamente os KPIs.

### Fase 2 — Postura operacional

Saúde SCCM, enrollment, compliance, configurações, aplicações obrigatórias, patches, celulares e tokens Apple.

### Fase 3 — Governança

Histórico, regras, responsáveis, SLA, exceções, notificações e vínculo com chamados no ServiceNow.

### Fase 4 — Enriquecimento

Cortex/Defender, CMDB, Autopilot, Log Analytics, Endpoint Analytics e recursos avançados contratados.

### Homologação final

- [ ] Totais reproduzíveis nas fontes e cruzamentos corretos em amostras conhecidas.
- [ ] Indisponibilidade de integração não distorce os indicadores.
- [ ] Impacto no SCCM medido em teste de carga e dentro do limite acordado.

A recomendação se mantém: começar pelo inventário reconciliado e pela cobertura efetiva, base de confiança para todos os indicadores seguintes.

## 13. Instruções de trabalho para o Claude Code

Trabalhe fase a fase, com plano aprovado antes do código, testes automatizados e simuladores no lugar dos ambientes reais.

- Em cada fase, proponha plano (estrutura, decisões, riscos e testes) e aguarde aprovação; ao final, entregue relatório contra os critérios de aceite da seção 12.
- Nenhuma conexão a SCCM, Intune, Entra ou AD reais durante o desenvolvimento e nenhuma credencial real no repositório.
- Simuladores obrigatórios:
  - **Banco SCCM simulado:** script que cria, em SQL Server de teste (container), views com os mesmos nomes e colunas usados, populadas por gerador sintético com casos de borda.
  - **Mock do Graph (WireMock.Net):** paginação, 429, 403 por permissão, tokens com roles parciais e dispositivos em tenant attach.
  - **AD por interface,** com implementação fake em memória; teste real fica opcional e manual.
  - **Modo demonstração** do produto usando o mesmo gerador de dados.
- Código e identificadores em inglês; interface, mensagens e documentação em português do Brasil.
- Toda mensagem de erro segue o padrão: o que aconteceu, impacto e como resolver.
- Testes automatizados obrigatórios para reconciliação, regras, KPIs e etapas do motor de instalação, incluindo idempotência (rodar duas vezes dá o mesmo resultado).
- Ambiguidade nesta especificação: registre um ADR em `docs/adr` com a opção mais conservadora e siga.
- Mantenha o `CLAUDE.md` com os comandos de build, teste e empacotamento.
- Novas dependências distribuídas com o produto só com licença permissiva, registradas em THIRD-PARTY-NOTICES.

## 14. Decisões a confirmar

Até a confirmação, o desenvolvimento segue o padrão indicado; nenhuma destas decisões bloqueia a Fase 0.

| Tema | Padrão adotado | A confirmar |
|---|---|---|
| Banco do Nexus | PostgreSQL embarcado | Banco corporativo para produção (SQL Server ou PostgreSQL) |
| SQL do site SCCM | Detectado na instalação | Local ou remoto, o que define conta virtual ou gMSA |
| Topologia do SCCM | Site primário único | Existência de CAS ou de vários primários |
| Saída para a internet | Proxy WinHTTP detectado | Proxy, autenticação e liberação dos endpoints Microsoft |
| Consentimento no Entra | Caminho B (pacote para o administrador) | Quem consente as permissões de aplicativo |
| HTTPS | Certificado da AC corporativa | Modelo de certificado e nome DNS de acesso |
| Assinatura de código | Certificado corporativo | Processo de assinatura no pipeline |
| Licenciamento | Detectado e marcado por KPI | Intune, Entra P1/P2, Endpoint Analytics, Defender e Intune Suite |
| Gerenciamento esperado | Modelos da seção 8.1 | Estratégia real por grupo |
| Retenção | 90 a 180 dias e 13 meses | Necessidade de auditoria e volume |
| Ferramenta do MSI | WiX Toolset | Termos de licença atuais do WiX para uso corporativo |
