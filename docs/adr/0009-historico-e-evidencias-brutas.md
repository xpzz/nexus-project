# ADR-0009: histórico e retenção de evidências brutas

Status: aceito.

## Contexto

As tabelas de origem eram substituídas a cada coleta (com troca atômica: uma falha mantinha o último resultado). Isso protegia a cobertura, mas apagava o que cada fonte disse antes, e os ativos eram recriados a cada reconciliação. Não era possível explicar por que um ativo foi classificado de certa forma no passado.

## Decisão

Manter as tabelas de origem como "último estado" e **acrescentar**:

- `RawRecordVersion`: uma versão por registro de origem, nova só quando o conteúdo muda. Datas que mudam a cada coleta (`Last*`, `CollectedAt`) ficam fora da comparação (hash SHA-256 das propriedades restantes, em ordem estável). Registros que a fonte deixa de devolver são fechados (`IsCurrent = false`, `RemovedAt`), nunca apagados.
- `EvidenceTimelineEntry`: as datas informadas por cada fonte, no máximo uma por fonte e ativo a cada 12 horas.
- `AssetChange`: mudanças relevantes entre reconciliações (estado, tipo, propriedade, gerenciamento, usuário, área, modelo, sistema, conformidade, MAM, confiança, criação e remoção). A primeira carga não registra mudanças.
- `JobRun`: cada execução com Run ID, duração, registros e mensagem. O Run ID vai para os logs (escopo de log) e para as versões arquivadas.

Retenção: `Collection.HistoryRetentionDays` (padrão 400, mínimo 30). A limpeza só remove versões antigas que já não são correntes.

## Limites declarados

- Versões são arquivadas para SCCM, AD, Intune, Entra (dispositivos e usuários), XDR, Netskope, MAM, políticas de proteção de apps, configurações de apps e Acesso Condicional. Estados de política por dispositivo e evidência de sign-in (volátil) não são arquivados.
- Falha ao arquivar não falha a coleta (aviso no log): o histórico daquela rodada fica incompleto.
- O crescimento depende da taxa de mudança real dos dados, não do número de coletas.
