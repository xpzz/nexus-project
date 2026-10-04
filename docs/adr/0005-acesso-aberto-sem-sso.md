# ADR-0005: Acesso aberto às telas de inventário enquanto não há SSO

Status: aceita (transitória, até o SSO do Entra ID)

## Contexto

O modo de configuração mandava todo acesso remoto para a tela do código de uso único. O usuário pediu "sem login por enquanto" para consultar o inventário pelo navegador.

## Decisão

- `web.openAccess` (padrão `true`) libera as telas de **somente leitura** (painel, parque, BYOD, inventário, dispositivo, pendências, SCCM e Intune) para quem alcança o site.
- Continuam exigindo o próprio servidor (localhost) ou o código de configuração: Assistente, Saúde do Nexus (pausar coletores, pedir coleta) e `/diagnostico` (logs e configuração).
- A regra está em `AccessPolicy` (testada). As telas do operador também usam `[Authorize]`, então a navegação dentro da sessão Blazor não contorna o bloqueio.
- Para fechar tudo de novo: `"web": { "openAccess": false }` em `config/nexus.json`.

## Consequências

- Quem tem acesso de rede ao site vê nomes de equipamentos e usuários. Restrinja pelo firewall do Windows (porta HTTPS) até o SSO existir.
- O SSO substitui esta decisão; os perfis (Administração, Gestão, Operação, Segurança) e o mascaramento de BYOD entram junto.
