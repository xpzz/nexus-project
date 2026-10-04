# ADR-0006: Pool de máquinas ativas por correlação das datas de todas as ferramentas

Status: aceita

## Contexto

"Ativo" era "qualquer fonte reportou nos últimos 30 dias". Isso trata igual o cliente SCCM que acabou de sincronizar e o `lastLogonTimestamp` do AD, que é replicado com até duas semanas de atraso e muda por causa da senha da conta de máquina. Cobertura e pendências dependem de uma lista de máquinas ativas confiável.

## Decisão

Cada ferramenta entrega uma data de último report. Elas se dividem em:

- **Evidência forte** (roda no equipamento): cliente SCCM, sincronização do Intune, agente Cortex XDR, cliente Netskope, aplicativo protegido por MAM.
- **Evidência fraca** (lado da identidade): último logon do AD e login aproximado do Entra ID.

Dentro da janela (padrão 30 dias, `collection.activityWindowDays`), cada máquina recebe uma classe:

| Classe | Regra | No pool |
|---|---|---|
| Confirmada | duas ou mais fontes reportaram | sim |
| Uma fonte | só uma fonte forte reportou | sim |
| Não confirmada | só uma fonte fraca reportou | não |
| Inativa | nenhuma fonte reportou | não |

O pool é Confirmada + Uma fonte e é o denominador dos indicadores. "Não confirmada" aparece à parte, com estado próprio, e continua visível para as regras de lacuna (corporativo sem gestão). Inativa gera "Sem comunicação".

A tela Pool ativo mostra, para cada ferramenta, quantas máquinas do pool ela viu na janela, quantas conhece sem reportar e quantas não conhece. As divergências (cliente SCCM mudo com outras ferramentas ativas, agente XDR parado) viram pendências.

## Netskope e XDR no vínculo

Netskope e XDR não trazem o ID do Entra. Ligam-se por: ID de gerenciamento do Netskope igual ao ID do Entra ou do Intune (forte), número de série válido (médio) e nome do host sem sufixo DNS (apoio, baixa confiança, só quando o nome é único). Vários agentes do mesmo host ficam no mesmo dispositivo e geram revisão informativa.

## Consequências

- A contagem de "ativos" cai onde só o AD ou o Entra viam a máquina; elas aparecem como "não confirmadas", não como inativas.
- Ferramenta fora do ar não derruba a cobertura: ela some do cruzamento e a máquina continua pelas outras.
- O limite de evidência forte e fraca está no código (`ActivityModel`) e é fácil de ajustar.
