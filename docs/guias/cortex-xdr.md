# Cortex XDR no Azul Nexus

O Nexus lê a tabela de endpoints que a rotina do XDR (`get_endpoints`) já grava no SQL. Ele **não guarda a chave da API do XDR** e não chama a API: só lê a tabela, com a conta do serviço Worker, em modo somente leitura.

## O que entra no inventário

- Cada linha da tabela vira um agente. Ele é ligado ao dispositivo pelo **nome** (sem o sufixo DNS), só quando o nome identifica um único dispositivo; é evidência de apoio, de baixa confiança, como o AD.
- Vários agentes com o mesmo nome (reinstalação) ficam no mesmo dispositivo; o conectado é o exibido e o caso entra na fila de revisão.
- Máquina que só existe no XDR vira um dispositivo corporativo sem gestão.
- O `last_seen` do agente conta como atividade do dispositivo, ao lado de SCCM, Intune, Entra ID e AD.
- `last_seen` é lido como UTC (a rotina converte o valor Unix com `FromUnixTimeMilliseconds(...).DateTime`).

## Regras e indicadores novos

- **Cobertura de EDR (Cortex XDR)**: Windows corporativo ativo com agente conectado ÷ Windows corporativo ativo (meta 98%).
- **Sem agente Cortex XDR** (Alta): Windows corporativo ativo conhecido por outra fonte e sem agente.
- **Agente XDR sem conexão ou sem reportar** (Média): status diferente de CONNECTED, ou as outras fontes viram o dispositivo mais de 7 dias depois do agente.
- Funil do EDR na tela SCCM e Intune e chip "Cortex XDR" no topo.

## Configuração (no servidor, como administrador)

```powershell
nexusctl xdr-configure --server sccmdbsutb01p --database cortex_db --table API_Cortex_getAllEndpoints
nexusctl xdr-grant-script --account "AZUL\svc.sccm" --output xdr-grant.sql   # entregue ao DBA, ou:
nexusctl xdr-grant --account "AZUL\svc.sccm"                                # aplica com a sua identidade
nexusctl collect xdr --wait
nexusctl test
```

Para desligar: `nexusctl xdr-configure --off`.

## Atenção com a rotina que alimenta a tabela

- Ela só insere e atualiza: agentes removidos do XDR continuam na tabela. O `last_seen` antigo os mantém fora da cobertura, mas eles aparecem como "desatualizados".
- Se a rotina parar, a verificação de saúde do Nexus avisa quando o agente mais recente tem mais de 24 horas.
- A rotina monta o SQL por concatenação de texto. Um nome com apóstrofo quebra o comando; vale trocar por comando parametrizado (`SqlCommand` com `@parametros`).
