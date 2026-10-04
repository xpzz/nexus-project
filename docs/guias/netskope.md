# Netskope no Azul Nexus

O Nexus lê os clientes (agentes) do Netskope pela API REST do tenant ("Get Client Data") e usa a data do último evento de cada cliente no pool de máquinas ativas.

## Configurar (no servidor, como administrador)

```powershell
# Se o servidor só sai à internet pelo proxy corporativo (vale para o Netskope e para o Microsoft Graph):
nexusctl network-proxy --url http://proxy.azul.corp:8080

nexusctl netskope-configure --tenant azul.goskope.com      # pede o token sem mostrá-lo
nexusctl netskope-test                                     # mostra a estrutura da resposta
nexusctl collect netskope --wait
nexusctl test
```

- O token fica protegido por DPAPI (escopo da máquina); não aparece na configuração exportada nem nos logs. Para desenvolvimento use a variável `NEXUS_NETSKOPE_TOKEN`.
- O caminho padrão é `/api/v1/clients` com `token=` na URL e paginação por `limit` e `skip`. Se o seu tenant usa a API v2 ou outro caminho: `nexusctl netskope-configure --path <caminho> --token-placement header --offset-parameter offset`.
- `nexusctl netskope-test` imprime só os nomes dos campos do primeiro registro e quantos registros o Nexus reconheceu (nome do host, data do último evento, serial). Se o número de reconhecidos vier baixo, me mande a saída: o mapeamento aceita vários formatos, mas depende do que o seu tenant devolve.
- Desligar: `nexusctl netskope-configure --off`.

## O que entra no inventário

- Ligação ao dispositivo: ID de gerenciamento igual ao do Entra ou do Intune (forte), serial válido (médio), nome do host sem sufixo (apoio).
- Último evento do cliente conta como atividade (evidência forte).
- Regra "Sem cliente Netskope" (Média) para computador corporativo ativo, só com a coleta ligada.

## Atenção

- A API v1 recebe o token na URL; o Nexus nunca o escreve em log nem em mensagem de erro, mas proxies intermediários podem registrá-lo. Prefira o cabeçalho (v2) se o seu tenant permitir.
- `last_event` é o último evento do cliente, não um heartbeat garantido: um cliente estável pode ter evento antigo. Por isso o Netskope é uma fonte entre várias, e o pool só exige uma fonte forte.
