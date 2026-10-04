# ADR-0008: papéis e login com Microsoft Entra ID

Status: aceito. Complementa o ADR-0005 (acesso aberto sem SSO), que continua sendo o padrão até o login ser validado.

## Decisão

Quatro papéis, funções de aplicativo do registro "Azul Nexus – Web":

| Papel | Pode |
|---|---|
| `Nexus.Leitura` | Ler o inventário e os indicadores. Nomes de donos de BYOD aparecem mascarados (LGPD). |
| `Nexus.Analista` | Tudo da Leitura, mais ver dados pessoais, salvar filtros, exportar CSV e registrar exceções. |
| `Nexus.AdminIntegracao` | Tudo do Analista, mais coleta manual por conector, saúde do Nexus, assistente e logs técnicos. |
| `Nexus.Auditoria` | Ler a trilha de auditoria. Quem administra não lê a própria trilha. |

`Web.AuthMode`: `open` (padrão) ou `entra`. Em `entra`, todo visitante entra pelo Microsoft Entra ID (código de autorização com PKCE). O aplicativo se autentica com o **certificado** do registro web (asserção de cliente assinada), sem segredo. Os papéis vêm da claim `roles`. Sem papel, a pessoa vê o que aconteceu, o impacto e como resolver. O acesso do servidor (localhost e código de configuração) continua válido em qualquer modo.

Toda ação administrativa (filtro salvo, exportação, coleta manual, exceção, exportação da própria auditoria) grava um evento de auditoria com quem, quando e o quê.

## Consequências

- `CurrentAccess` é o único lugar que decide permissões; páginas e endpoints só perguntam.
- Em modo aberto, visitantes remotos são Leitura: não exportam, não salvam filtros, não registram exceções.
- O login nunca foi executado contra um tenant real: validar em homologação antes de ligar `entra`.
- Pacote novo: `Microsoft.AspNetCore.Authentication.OpenIdConnect` (MIT).
