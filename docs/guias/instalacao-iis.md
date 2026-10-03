# Instalação do Azul Nexus no IIS

> Primeira instalação em servidor sem Git nem ferramentas? Siga o [tutorial passo a passo](tutorial-instalacao-servidor.md). Este documento é a referência.

Implantação por `dotnet publish`, sem instalador (ADR-0001). O site roda no IIS (pool e porta próprios) e a coleta roda como serviço do Windows.

## 1. O que o servidor precisa ter

| Item | Observação |
|---|---|
| Windows Server 2012 R2 ou superior, 64 bits | Recomendado 2019 ou mais novo |
| IIS com **WebSockets** e **Ferramentas de script** | `Install-WindowsFeature Web-Server, Web-WebSockets, Web-Scripting-Tools -IncludeManagementTools` (servidores de site do SCCM com MP/DP já têm o IIS) |
| **ASP.NET Core 10 Hosting Bundle** | Instalar o `dotnet-hosting-10.x-win.exe` **em janela de manutenção**: ele reinicia o IIS e, em servidor de site do SCCM, interrompe por instantes o management point e o distribution point. O script só o instala se você pedir (`-HostingBundleInstaller` + `-AllowIisRestart`) |
| Banco do Nexus | SQL Server ou PostgreSQL já existente. **Não use a instância licenciada com o Configuration Manager** (o script bloqueia). O banco é criado se a conta de quem instala puder; senão, o script gera o T-SQL para o DBA |
| Certificado HTTPS | Certificado de servidor com o nome DNS de acesso e chave privada em `LocalMachine\My` (AC corporativa) |
| .NET SDK | **Não é necessário** no servidor; só na máquina que gera o pacote |

O Worker e o `nexusctl` são *self-contained*: não dependem de runtime instalado.

## 2. Gerar o pacote (máquina de build)

```powershell
pwsh deploy/Publish-AzulNexus.ps1                       # gera artifacts\AzulNexus-<versão>\ e .zip
pwsh deploy/Publish-AzulNexus.ps1 -CodeSigningThumbprint <thumbprint> -TimestampServer <url>   # com assinatura de código (Windows)
```

O pacote contém `web\` (site), `app\` (Worker e `nexusctl`), `deploy\` (scripts) e `docs\`, mais `SHA256SUMS.txt`.

## 3. Instalar (no servidor, PowerShell como administrador)

1. Copie e descompacte o `.zip`.
2. `Copy-Item deploy\install.sample.json deploy\install.json` e ajuste. O mínimo é `database.server`; todo o resto tem padrão ou detecção automática.
3. Valide sem alterar nada: `.\deploy\Install-AzulNexus.ps1 -DetectOnly`
4. Instale: `.\deploy\Install-AzulNexus.ps1`

Ao terminar: site no IIS em `https://<nome>:8443`, serviço `AzulNexus.Worker` em execução, banco migrado, permissões aplicadas, regra de firewall criada, primeira coleta pedida, e o **código de configuração** (uso único, 24 h) exibido no console (ele não vai para o log).

Parâmetros úteis: `-Answers <arquivo>`, `-HostingBundleInstaller <exe> -AllowIisRestart`, `-AllowSelfSigned` (somente piloto), `-DatabasePassword <SecureString>` ou variável `NEXUS_DB_PASSWORD` (PostgreSQL; a senha é guardada protegida por DPAPI), `-SkipBackup`, `-NoOpenBrowser`, `-LogPath`.

### Códigos de saída

| Código | Significado |
|---|---|
| 0 | Sucesso (veja as linhas `PENDENTE`, se houver) |
| 3010 | Reinício necessário |
| 10 | Bloqueio de pré-requisito (nada foi alterado) |
| 20 | Falha, com desfazer concluído |

### Arquivo de respostas (`install.json`)

| Campo | Padrão | Observação |
|---|---|---|
| `installDir` / `dataDir` | `auto` | Programas em `Program Files\Azul Nexus`; dados no maior volume fora do SO (`<vol>\AzulNexus`), senão `ProgramData` |
| `iis.siteName`, `iis.appPoolName` | `Azul Nexus`, `AzulNexus` | Site e pool exclusivos; sites do SCCM não são tocados |
| `iis.port` | `8443` | Portas 80, 443, 8530, 8531 e 10123 são recusadas |
| `iis.hostName` | `auto` | FQDN do servidor |
| `iis.certificate` | `auto` | `auto` escolhe o melhor certificado; ou informe o thumbprint; `self-signed` só em piloto |
| `database.provider` | `SqlServer` | Ou `PostgreSql` |
| `database.server`, `database.name` | (obrigatório), `AzulNexus` | |
| `serviceIdentity.webGmsa`, `workerGmsa` | `null` | Conta virtual quando o SQL acessado é local; **gMSA obrigatória** quando é remoto |
| `sccm.*` | `auto` | Site, SQL e banco detectados (registro e SMS Provider); informe à mão se o Nexus estiver fora do servidor do SCCM |
| `sccm.grantViewAccess` | `if-permitted` | Tenta conceder `SELECT` nas views; se não puder, gera o script para o DBA. `never` só gera o script |
| `activeDirectory.*` | `auto` | Domínio do servidor |
| `demoMode` | `false` | `true` usa dados sintéticos (sem SCCM/AD reais) |

## 4. Quem faz o quê (separação de funções)

Quando a conta de quem instala não tem permissão, o script **não falha**: grava o T-SQL em `<dados>\scripts` e lista a pendência.

| Arquivo | Para quem | O que faz |
|---|---|---|
| `nexus-db-grant.sql` | DBA do banco do Nexus | Cria o banco e dá leitura/escrita (sem DDL) às contas dos serviços |
| `nexus-db-migrations.sql` | DBA do banco do Nexus | Cria as tabelas (script idempotente das migrações) |
| `sccm-grant.sql` / `sccm-revoke.sql` | DBA do SQL do site | Papel `azul_nexus_reader` com `SELECT` explícito nas views usadas, e a reversão |

Depois de aplicar, rode `nexusctl test`: o teste roda no Worker, com a conta do serviço, e aponta o que ainda falta.

## 5. Atualizar e reparar

Rode o mesmo `Install-AzulNexus.ps1` com o pacote novo.
- Mesma versão = reparo (recria serviço, pool, site, permissões e firewall; dados intactos).
- Versão diferente = atualização: os binários antigos são copiados para `<dados>\backups\bin-<versão>-<data>`, o banco passa por backup antes da migração e o site fica offline (`app_offline.htm`) durante a troca. Se algo falhar, os binários anteriores são restaurados e o backup do banco continua disponível. Sem backup bem-sucedido a atualização é cancelada (use `-SkipBackup` só se o DBA já fez o backup).

## 6. Remover

```powershell
.\deploy\Uninstall-AzulNexus.ps1               # preserva dados, configuração, chaves e backups
.\deploy\Uninstall-AzulNexus.ps1 -RemoveData   # apaga também os dados (pede para digitar APAGAR)
```

Nada fora do servidor é removido sozinho. O script gera `scripts\sccm-revoke.sql` e `scripts\entra-limpeza.txt` (registros no Entra, logins SQL e banco, a remover pelos responsáveis).

## 7. Operação e diagnóstico

```powershell
nexusctl status                 # coletas e últimas verificações
nexusctl test                   # verificações executadas pelo Worker
nexusctl collect all --wait     # coletar agora
nexusctl pause | resume         # pausar ou retomar coletores
nexusctl diagnostics            # pacote de diagnóstico (logs, versões, configuração sem segredos)
nexusctl setup-code             # novo código de configuração (administrador local)
nexusctl recover-access         # reabre o modo de configuração, com auditoria
nexusctl config export -o nexus.json   # configuração sem segredos
```

Logs em `<dados>\logs` (JSON) e eventos críticos no Log de Eventos do Windows (origem `Azul Nexus`).

## 8. Problemas comuns

| Sintoma | Causa provável | Solução |
|---|---|---|
| Script sai com 10 e "Hosting Bundle ausente" | Falta o módulo do IIS / runtime 10 | Instale o Hosting Bundle 10.x em janela de manutenção, ou use `-HostingBundleInstaller` com `-AllowIisRestart` |
| HTTP 500.19 ou 500.0 no site | Módulo `AspNetCoreModuleV2` ausente ou pool sem permissão na pasta | Reinstale o Hosting Bundle e rode o script (reparo) |
| `/healthz` retorna `database-unreachable` | Conta do pool sem login no banco | Aplique `nexus-db-grant.sql` e reinicie o pool |
| Verificação “SCCM: leitura das views” com erro | Conta do Worker sem `SELECT` nas views | Aplique `sccm-grant.sql` no SQL do site |
| Banco do Nexus remoto e o script bloqueia | Conta virtual aparece como a conta de computador | Crie gMSAs e informe em `serviceIdentity` |
| Aviso do navegador no certificado | Certificado autoassinado ou nome diferente do acesso | Use certificado da AC com o nome DNS de `iis.hostName` |

## 9. Estado de validação desta versão

- Validado em Linux: geração do pacote `win-x64`, sintaxe dos scripts, 30+ testes das funções de decisão (certificado, identidades, SQL local/remoto, pasta de dados, arquivo resolvido) e os testes .NET.
- **Ainda não executado em um Windows Server real:** criação do site e do pool, serviço do Worker, ACLs, firewall, certificado e Job Object. Rode primeiro em homologação, com `-DetectOnly`, e informe o resultado.
- A detecção do SQL do site pelo SMS Provider é “melhor esforço”; se não detectar, informe `sccm.sqlServer` e `sccm.database`.
