# Tutorial: instalar o Azul Nexus em um servidor Windows "zerado"

Para um servidor que **não tem Git, .NET SDK nem nada instalado**. Você só precisa de acesso de administrador ao servidor (RDP ou console) e de um computador com internet para baixar dois arquivos.

> Tempo estimado: 30 a 60 minutos, a maior parte esperando downloads e a aprovação do DBA.
> Se o servidor é o **servidor de site do SCCM**, faça a etapa 3 (Hosting Bundle) em **janela de manutenção**: ela reinicia o IIS e derruba por instantes o management point e o distribution point.

---

## Etapa 0 — O que você vai precisar (checklist)

| Item | Quem fornece | Observação |
|---|---|---|
| Pacote `AzulNexus-X.Y.Z.zip` | Você baixa (etapa 1) | Não precisa de Git: é um ZIP pronto |
| `dotnet-hosting-10.x-win.exe` | Você baixa (etapa 1) | Instalador oficial da Microsoft do módulo do IIS e do runtime |
| Servidor SQL para o banco do Nexus | DBA | SQL Server (ou PostgreSQL). **Não** use a instância do SCCM. Anote o nome, ex.: `SQL-NEXUS01` |
| Nome de acesso (DNS) | Rede | Ex.: `nexus.suaempresa.local`, apontando para o servidor |
| Certificado HTTPS com esse nome | Equipe de PKI | Instalado no servidor em *Computador local › Pessoal*, com chave privada. Se ainda não houver, use `-AllowSelfSigned` **só para teste** |
| gMSA para o site e para o Worker | AD | **Só se** o SQL do Nexus ou o SQL do SCCM for **outro servidor** (etapa 6) |

---

## Etapa 1 — Baixar os arquivos (no seu computador, com internet)

### 1.1 Pacote do Azul Nexus

1. Abra https://github.com/xpzz/nexus-project/actions (precisa estar logado no GitHub com acesso ao repositório).
2. Clique na execução mais recente do workflow **CI** na branch `main` (marca verde).
3. Em **Artifacts**, baixe **AzulNexus-pacote**. O GitHub entrega um ZIP contendo o `AzulNexus-X.Y.Z.zip`; extraia o ZIP externo e guarde o `AzulNexus-X.Y.Z.zip` de dentro.

> Alternativa: se você tem o .NET 10 SDK em alguma máquina, rode `pwsh deploy/Publish-AzulNexus.ps1` no repositório e use o `artifacts\AzulNexus-X.Y.Z.zip`.

### 1.2 ASP.NET Core 10 Hosting Bundle

1. Abra https://dotnet.microsoft.com/download/dotnet/10.0
2. Na seção **ASP.NET Core Runtime**, baixe **Hosting Bundle** (arquivo `dotnet-hosting-10.x.x-win.exe`).

### 1.3 Levar para o servidor

Copie os dois arquivos para o servidor (RDP com unidade compartilhada, pasta de rede ou pendrive) para `C:\Instalacao\`.

---

## Etapa 2 — Preparar o servidor

Abra o **PowerShell como administrador** (botão direito › *Executar como administrador*) e rode:

```powershell
# 1) Descompactar o pacote
New-Item -ItemType Directory C:\Instalacao -Force | Out-Null
Expand-Archive C:\Instalacao\AzulNexus-*.zip -DestinationPath C:\Instalacao\AzulNexus -Force

# 2) Desbloquear os arquivos baixados da internet (senão o Windows bloqueia os scripts)
Get-ChildItem C:\Instalacao -Recurse | Unblock-File

# 3) Permitir scripts SOMENTE nesta janela do PowerShell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
```

> Se a sua empresa bloqueia scripts por GPO (a política `Set-ExecutionPolicy` acima é ignorada), peça à segurança para liberar a pasta `C:\Instalacao` ou assine os scripts (veja `Publish-AzulNexus.ps1 -CodeSigningThumbprint`).

### Ativar o IIS (se ainda não estiver ativo)

Servidores de site do SCCM com management point/distribution point já têm o IIS. Para conferir e completar:

```powershell
Install-WindowsFeature Web-Server, Web-WebSockets, Web-Scripting-Tools, Web-Mgmt-Console -IncludeManagementTools
```

(Se o IIS já existe, o comando só adiciona o que falta e **não** reinicia o IIS.)

---

## Etapa 3 — Instalar o Hosting Bundle

**Em servidor de site do SCCM: só em janela de manutenção.**

```powershell
Start-Process C:\Instalacao\dotnet-hosting-10*-win.exe -ArgumentList '/install','/quiet','/norestart' -Wait
iisreset
```

Confira:

```powershell
Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"   # deve dar True
Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.AspNetCore.App"      # deve listar 10.x
```

> Alternativa: deixe o script de instalação instalar para você, adicionando `-HostingBundleInstaller C:\Instalacao\dotnet-hosting-10.x.x-win.exe -AllowIisRestart` na etapa 7 (isso autoriza o reinício do IIS).

---

## Etapa 4 — Banco do Nexus (com o DBA)

O Nexus precisa de **um banco só dele**. Opções:

- **O DBA cria o banco e o acesso** — o script de instalação gera o T-SQL pronto se a sua conta não puder criar (etapa 8); ou
- **Sua conta pode criar** no SQL Server de destino (ela precisa de permissão `CREATE ANY DATABASE` e de criação de logins) — o script faz tudo.

Anote o nome do servidor SQL (ex.: `SQL-NEXUS01` ou `SQL-NEXUS01\INSTANCIA`).

---

## Etapa 5 — Certificado HTTPS

Confira se existe um certificado com o nome de acesso:

```powershell
Get-ChildItem Cert:\LocalMachine\My | Where-Object HasPrivateKey |
  Select-Object Subject, NotAfter, Thumbprint, @{n='Nomes';e={$_.DnsNameList.Unicode -join ', '}}
```

- Achou um com o nome (ex.: `nexus.suaempresa.local`), válido e com chave privada: o script o escolhe sozinho (`"certificate": "auto"`), ou você informa o `Thumbprint`.
- Não achou: peça à PKI um certificado de servidor com esse nome DNS. **Só para teste**, use `-AllowSelfSigned` na etapa 7 (o navegador mostrará aviso de segurança).

---

## Etapa 6 — gMSA (somente se o SQL for remoto)

O Nexus nunca usa a "conta virtual" para falar com um SQL em **outro servidor**, porque ela aparece na rede como a conta do computador (no SCCM, costuma ser administradora do SQL). Nesse caso peça ao AD duas gMSAs (ex.: `CORP\nexus-web$` e `CORP\nexus-worker$`) e instale-as no servidor:

```powershell
Install-ADServiceAccount nexus-web
Install-ADServiceAccount nexus-worker
Test-ADServiceAccount nexus-web; Test-ADServiceAccount nexus-worker   # ambos True
```

Se o SQL do Nexus **e** o SQL do SCCM estiverem no próprio servidor, **pule esta etapa**.

---

## Etapa 7 — Configurar e instalar

### 7.1 Arquivo de respostas

```powershell
cd C:\Instalacao\AzulNexus\deploy
Copy-Item install.sample.json install.json
notepad install.json
```

Ajuste **apenas o que for necessário**. Exemplo mínimo (SQL Server, tudo local ou detectado):

```json
{
  "installDir": "auto",
  "dataDir": "auto",
  "iis": { "siteName": "Azul Nexus", "appPoolName": "AzulNexus", "port": 8443, "hostName": "nexus.suaempresa.local", "certificate": "auto", "openFirewall": true },
  "database": { "provider": "SqlServer", "server": "SQL-NEXUS01", "name": "AzulNexus" },
  "serviceIdentity": { "webGmsa": null, "workerGmsa": null },
  "sccm": { "site": "auto", "sqlServer": "auto", "database": "auto", "grantViewAccess": "if-permitted" },
  "activeDirectory": { "domain": "auto" },
  "demoMode": false
}
```

- SQL em outro servidor: preencha `serviceIdentity` com as gMSAs da etapa 6 (`"webGmsa": "CORP\\nexus-web$"`, `"workerGmsa": "CORP\\nexus-worker$"`).
- Nexus em servidor que **não** é o do SCCM: preencha `sccm.site`, `sccm.sqlServer` e `sccm.database` (ex.: `"AZ1"`, `"SQL-SCCM01"`, `"CM_AZ1"`).
- Só quer ver a interface com dados de exemplo: `"demoMode": true`.

### 7.2 Validar sem alterar nada

```powershell
.\Install-AzulNexus.ps1 -DetectOnly
```

Cada item aparece como **OK**, **Atenção** ou **Bloqueio**, com o "Como resolver". Resolva os **Bloqueio** e rode de novo até não haver nenhum. Se der o código de saída `10`, nada foi alterado.

### 7.3 Instalar

```powershell
.\Install-AzulNexus.ps1
```

Variações:

```powershell
# deixando o script instalar o Hosting Bundle (reinicia o IIS!)
.\Install-AzulNexus.ps1 -HostingBundleInstaller C:\Instalacao\dotnet-hosting-10.x.x-win.exe -AllowIisRestart

# teste com certificado autoassinado
.\Install-AzulNexus.ps1 -AllowSelfSigned

# banco PostgreSQL (a senha é pedida e guardada protegida)
.\Install-AzulNexus.ps1
```

No final o script mostra o endereço (`https://nexus.suaempresa.local:8443`) e o **código de configuração** (uso único, vale 24 h). **Anote o código**: ele não vai para o arquivo de log.

---

## Etapa 8 — Pendências do DBA (se houver)

Se o script terminar com linhas `PENDENTE`, a sua conta não tinha permissão em algum SQL. Os scripts estão prontos em `<pasta de dados>\scripts` (a pasta de dados aparece no log; por padrão `D:\AzulNexus` ou `C:\ProgramData\AzulNexus`):

| Arquivo | Entregue ao DBA de | O que faz |
|---|---|---|
| `nexus-db-grant.sql` | Banco do Nexus | Cria o banco e dá leitura/escrita às contas dos serviços |
| `nexus-db-migrations.sql` | Banco do Nexus | Cria as tabelas |
| `sccm-grant.sql` | SQL do site SCCM | Papel somente leitura nas views usadas (reversão em `sccm-revoke.sql`) |

Depois que o DBA aplicar, **reinicie e verifique**:

```powershell
Restart-Service AzulNexus.Worker
Restart-WebAppPool AzulNexus
nexusctl test
```

---

## Etapa 9 — Conferir que está funcionando

```powershell
nexusctl test          # verificações executadas pelo Worker, com a conta do serviço
nexusctl status        # coletas, registros e próxima execução
Get-Service AzulNexus.Worker
Invoke-WebRequest https://nexus.suaempresa.local:8443/healthz -UseBasicParsing   # {"status":"ok",...}
```

(Se abrir um novo PowerShell, o comando `nexusctl` já estará no PATH; se não, use `C:\Program Files\Azul Nexus\app\nexusctl.exe`.)

Esperado:
- `Banco do Nexus`: OK.
- `SCCM: leitura das views`: OK (ou "Atenção" listando views que o seu site não tem).
- `Active Directory`: OK.
- `Azure: Entra ID e Intune`: **Pendente** — é esperado; essa etapa vem depois.

Abra o navegador em `https://nexus.suaempresa.local:8443`:
- **No próprio servidor** não precisa de código.
- **De outro computador**, digite o código de configuração. Se perdeu: no servidor, `nexusctl setup-code`.

Você verá o **Assistente de configuração** (SCCM e AD concluídos, os demais pendentes) e a página **Saúde**.

---

## Etapa 10 — Atualizar para uma nova versão

Baixe o novo pacote (etapa 1.1), descompacte numa pasta nova e rode **o mesmo** `Install-AzulNexus.ps1` (com o seu `install.json`). Ele faz backup do banco antes de migrar e restaura os binários anteriores se algo falhar. Nada de configuração ou dados é perdido.

## Remover

```powershell
C:\"Program Files"\"Azul Nexus"\deploy\Uninstall-AzulNexus.ps1            # preserva os dados
C:\"Program Files"\"Azul Nexus"\deploy\Uninstall-AzulNexus.ps1 -RemoveData # apaga tudo (pede confirmação)
```

---

## Se algo der errado

| O que você vê | O que fazer |
|---|---|
| `.ps1 cannot be loaded because running scripts is disabled` | Rode `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force` na mesma janela |
| `... is not digitally signed` / aviso de segurança ao abrir | Rode o `Unblock-File` da etapa 2 |
| Saiu com código **10** | Nenhuma alteração foi feita. Leia as linhas `Bloqueio` e o "Como resolver" |
| Saiu com código **20** | Houve falha e o script desfez o que fez. O motivo está no console e no log (`<dados>\logs\install-*.log`). Corrija e rode de novo (é seguro repetir) |
| Site responde **500.19** ou **502** | Hosting Bundle ausente ou o pool sem acesso. Rode `iisreset` e depois o instalador de novo (ele repara) |
| `/healthz` mostra `database-unreachable` | A conta do site não tem login no banco: aplique `nexus-db-grant.sql` e rode `Restart-WebAppPool AzulNexus` |
| Aviso de certificado no navegador | Certificado autoassinado, ou o nome acessado é diferente do certificado |

**Para pedir ajuda**, gere o pacote de diagnóstico (não contém senhas) e envie junto com o log da instalação:

```powershell
nexusctl diagnostics
```

> **Importante:** esta versão ainda não foi testada em um Windows Server real. Na primeira instalação, rode em homologação, comece com `-DetectOnly` e envie o resultado para ajustarmos o que o seu ambiente exigir.
