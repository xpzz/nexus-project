# ADR-0004: Reconciliação de dispositivos (SCCM, Intune, Entra ID, AD)

Status: aceita

## Contexto

A SPEC (7.2/7.3) exige um ativo único por equipamento, sem juntar registros por nome. O nome muda (renomeação, reinstalação) e se repete (clones, equipamentos substituídos), e juntar por nome infla ou esconde a cobertura.

## Decisão

Os registros de gerenciamento (SCCM, Intune, Entra) só são unidos por evidência forte, nesta ordem:

1. **ID do dispositivo no Entra**: `AADDeviceID` do SCCM = `azureADDeviceId` do Intune = `deviceId` do Entra. GUID vazio nunca identifica nada. Confiança alta.
2. **Número de série válido**, com fabricante compatível. Valores como "To be filled by O.E.M.", "Default string", zeros e "None" são descartados. Dois dispositivos SCCM distintos com o mesmo serial **não** são unidos: vão para a fila de revisão. Confiança média.
3. **UUID de hardware**, só no caso conservador (um registro obsoleto e exatamente um registro ativo). Vários ativos com o mesmo UUID são suspeita de clone de VM e vão para revisão. Confiança média.

O **AD** é a exceção: é identidade de domínio, não gerenciamento. Ele se associa pelo nome apenas quando exatamente um objeto do AD e um ativo carregam esse nome; é marcado como evidência de apoio de baixa confiança (`AdByNameOnly`) e não reduz a confiança da junção dos registros de gerenciamento. Nome ambíguo vai para revisão.

Canais são fatos independentes: estar no Entra não é estar no Intune; `configurationManagerClient` (tenant attach) e `msSense` (Defender) não são MDM.

Os identificadores dos ativos são estáveis: reaproveitam o `asset_links` da execução anterior.

## KPIs

Cobertura SCCM, SCCM saudável, Intune MDM e completa usam denominadores explícitos (Windows corporativo **ativo**; MDM só para clientes Windows, não servidores). Desatualizados, pessoais e itens em revisão aparecem separados. Fonte sem coleta bem-sucedida resulta em "não habilitado", nunca em 0%.

## Consequências

- Menos junções automáticas e mais itens para revisão; é o lado seguro para um inventário de cobertura.
- Reinstalação com troca de ID e sem serial válido pode aparecer como dois ativos até haver UUID/ID em comum.
