# ADR-0007: motor de evidências para decidir se um equipamento está ativo

Status: aceito. Substitui a janela única de atividade do ADR-0006.

## Contexto

A Azul não tem CMDB confiável. Um registro no AD, no Entra ID ou no SCCM não prova que o equipamento existe e está em uso. O pool de máquinas ativas do ADR-0006 usava uma janela única de 30 dias para todos os tipos de aparelho, sem explicar a decisão.

## Decisão

Cada ativo recebe um **estado operacional** calculado por um motor de evidências, com **limiares configuráveis por tipo**, um **score explicável** e uma **justificativa em português** com as datas de cada fonte.

### Estados

| Estado | Regra |
|---|---|
| Ativo confirmado | Pelo menos `MinConfirmedSources` (2) sinais independentes dentro da janela de confirmação, ao menos um de telemetria do equipamento. |
| Ativo provável | Um sinal de telemetria na janela de probabilidade, ou dois sinais de identidade. |
| Sem evidência recente | Algum relato na janela de observação, nada recente. |
| Inativo ou obsoleto | Nenhum relato na janela de observação. Passado o limite de descomissionamento, vira "candidato à inativação". |
| Conflitante | A identidade é ambígua (item de revisão). A atividade pelas evidências continua registrada. |
| Desconhecido | Nenhuma fonte trouxe data. |
| Excluído ou descomissionado | Obsoleto no SCCM ou desabilitado no AD, sem telemetria recente. |

### Sinais e pesos

Telemetria: SCCM (maior data entre atividade, heartbeat DDR, política e inventário de hardware do cliente), Intune (última sincronização), Cortex XDR, Netskope, proteção de apps (MAM) e acesso ao Microsoft 365 por Entra Device ID. Identidade: Entra ID (último login) e AD (último logon). Cada sinal vale `confiabilidade × frescura`; frescura é 1 até a janela de confirmação, 0,6 até a de probabilidade, 0,25 até a de observação e 0 depois. O score é `100 × (1 − Π(1 − p))`: a chance de pelo menos um sinal estar certo. Confiabilidades padrão: SCCM 0,75, Intune 0,80, XDR 0,80, Netskope 0,70, MAM 0,55, M365 0,60, Entra 0,40, AD 0,25.

### Configuração (`nexus.json`, seção `Evidence`)

Nenhuma data fica no código. `Default` (7/30/90/180 dias) e `ByType` (celular e tablet 14/45/90/180; compartilhado, quiosque e IoT 14/60/120/240), `Reliability`, `MinConfirmedSources` e `TypeNameTokens`. Os degraus são normalizados para ficarem em ordem crescente.

### Tipos de ativo

desktop, notebook, servidor, celular, tablet, Mac, compartilhado, quiosque, IoT e não identificado. Vêm, nesta ordem, do sistema, do chassi do SCCM, do fabricante e modelo e de prefixos de nome configurados. **BYOD é propriedade, não tipo**: um iPhone pessoal é celular e BYOD.

### Identidade, atividade e risco são coisas diferentes

Confiança da identidade (`Confidence`), atividade (estado e score) e risco de segurança e gestão (índice de saúde das pendências) ficam em campos e telas separados. Nenhum score os mistura.

## Consequências

- Todo número de atividade tem explicação e lista.
- `IsActive` (usado pelos índices de saúde) continua igual à janela de probabilidade do tipo, por teste de paridade.
- Mudar um limiar muda o resultado na próxima reconciliação, sem recompilar.
- O motor mede evidência, não verdade: sem coleta, o estado é "desconhecido" ou "sem evidência", nunca "ativo".
