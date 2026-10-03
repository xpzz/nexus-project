# ADR-0002 — Conta única `svc.sccm` para o site e o Worker; Azure fora dela

- Data: 2026-10-03
- Situação: aceita (decisão do responsável pelo produto)
- Altera: SPEC §5.2 (identidades separadas por privilégio) e as premissas 9 e 10 no que diz respeito à identidade do site

## Decisão

1. O **site** (pool `AzulNexus` no IIS) e o **Worker** (serviço do Windows) rodam sob **uma única conta de domínio**, `svc.sccm`
   (`serviceIdentity.account` em `install.json`; aceita `svc.sccm` ou `DOMINIO\svc.sccm`). Não há contas virtuais nem gMSA nesse modo,
   e nenhuma conta nova é criada.
2. Essa conta é usada para: banco do Nexus (SQL Server), views do SCCM, Active Directory e pastas do Nexus. O instalador:
   valida a senha no domínio antes de alterar o servidor, concede localmente "Fazer logon como serviço", inclui a conta em `IIS_IUSRS`
   e concede nela o acesso ao banco do Nexus e às views do SCCM (idempotente; gera script para o DBA se não puder).
3. A senha é pedida no console (ou `-ServiceAccountPassword` / `NEXUS_SERVICE_PASSWORD`) e **nunca** vai para log nem para arquivo do Nexus.
   O Windows a guarda protegida (LSA para o serviço; `applicationHost.config` criptografado para o pool).
4. **Intune e Entra ID (Microsoft Graph) não usam `svc.sccm`.** O acesso é sempre por **identidade de aplicativo** do Entra
   (registro "Azul Nexus – Coletor", SPEC §4.4), autenticada por **certificado**: o Graph enxerga o aplicativo, nunca a conta do Windows.
   Consequência: a conta `svc.sccm` só precisa **ler a chave privada** do certificado do Coletor, e nenhum usuário ou senha do Windows é enviado ao Azure.
   Esta ADR cobre apenas a decisão de identidade; a tela do Azure ainda não foi implementada.

## Riscos aceitos

- `svc.sccm` costuma ser privilegiada no SCCM e no SQL do site. O site (interface web) passa a rodar com ela, o que amplia a exposição:
  uma falha na interface herdaria esse acesso. O Nexus só executa `SELECT`, e o instalador mostra um aviso de "Atenção" sobre isso.
- Mitigação recomendada: quando possível, trocar para uma conta própria com `SELECT` apenas nas views usadas (basta mudar `serviceIdentity.account` e rodar o instalador de novo).
- Se uma GPO controla "Fazer logon como serviço", a concessão local é sobrescrita: inclua a conta na GPO.
