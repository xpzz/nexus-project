# Tutorial: instalar o Azul Nexus em um servidor Windows "zerado"

Para um servidor que **não tem Git, .NET SDK nem nada instalado**. Você só precisa de acesso de administrador ao servidor (RDP ou console) e de um computador com internet para baixar dois arquivos.

> Tempo estimado: 30 a 60 minutos, a maior parte esperando downloads e a aprovação do DBA.
> Se o servidor é o **servidor de site do SCCM**, faça a etapa 3 (Hosting Bundle) em **janela de manutenção**: ela reinicia o IIS e derruba por instantes o management point e o distribution point.

---

## Etapa 0 — O que você vai precisar (checklist)

| Item | Quem fornece | Observação |
|---|---|---|
| Pacote `AzulNexus-X.Y.Z.zip` | Você baixa (etapa 1) | ZIP pronto da página de Releases, **sem Git e sem login**. Já traz o Hosting Bundle em `prereq\` |
| Servidor SQL para o banco do Nexus | DBA | SQL Server (ou PostgreSQL). **Não** use a instância do SCCM. Anote o nome, ex.: `SQL-NEXUS01` |
| Nome de acesso (DNS) | Rede | Ex.: `nexus.suaempresa.local`, apontando para o servidor |
| Certificado HTTPS com esse nome | Equipe de PKI | Instalado no servidor em *Computador local › Pessoal*, com chave privada. Se ainda não houver, use `-AllowSelfSigned` **só para teste** |
| gMSA para o site e para o Worker | AD | **Só se** o SQL do Nexus ou o SQL do SCCM for **outro servidor** (etapa 6) |

> Não existe um `.exe` instalador: a instalação é o duplo clique em **`Instalar.cmd`**, que roda o script `Install-AzulNexus.ps1` com tudo o que ele precisa.

---

## Etapa 1 — Baixar o pacote (no seu computador, com internet)

1. Abra **https://github.com/xpzz/nexus-project/releases** (o repositório é público: não precisa de login).
2. Na versão mais recente (**Azul Nexus X.Y.Z**), em **Assets**, baixe **`AzulNexus-X.Y.Z.zip`**. Se quiser conferir a integridade, baixe também o `.sha256` e compare: `Get-FileHash AzulNexus-X.Y.Z.zip -Algorithm SHA256`.
3. Copie o ZIP para o servidor (RDP com unidade compartilhada, pasta de rede ou pendrive), por exemplo para `C:\Instalacao\`.

> **Se a página de Releases ainda não tiver nenhuma versão:** abra https://github.com/xpzz/nexus-project/actions/workflows/release.yml, clique em **Run workflow** (botão à direita), aguarde uns 5 minutos e atualize a página de Releases.
>
> **Se o pacote não trouxer a pasta `prereq\` com o `dotnet-hosting-win.exe`** (o download automático da Microsoft pode falhar), baixe você mesmo o *Hosting Bundle* em https://dotnet.microsoft.com/download/dotnet/10.0 (seção **ASP.NET Core Runtime › Hosting Bundle**) e coloque-o em `AzulNexus\prereq\` depois de descompactar.

---

## Etapa 2 — Descompactar no servidor

Abra o **PowerShell como administrador** (botão direito › *Executar como administrador*) e rode:

```powershell
Expand-Archive C:\Instalacao\AzulNexus-*.zip -DestinationPath C:\Instalacao\AzulNexus -Force
Get-ChildItem C:\Instalacao -Recurse | Unblock-File     # remove o bloqueio de arquivos baixados da internet
Get-ChildItem C:\Instalacao\AzulNexus                  # deve mostrar: Instalar.cmd, Verificar-Ambiente.cmd, web, app, deploy, prereq...
```

(Ou clique com o botão direito no ZIP › *Extrair tudo*. O `Instalar.cmd` também desbloqueia os arquivos sozinho.)

> Se a sua empresa bloqueia scripts por GPO, peça à segurança para liberar a pasta `C:\Instalacao` ou assine os scripts (`Publish-AzulNexus.ps1 -CodeSigningThumbprint`).

### IIS

Servidores de site do SCCM com management point ou distribution point já têm o IIS. Para conferir e completar o que faltar (não reinicia o IIS):

```powershell
Install-WindowsFeature Web-Server, Web-WebSockets, Web-Scripting-Tools, Web-Mgmt-Console -IncludeManagementTools
```

---

## Etapa 3 — Hosting Bundle

O Nexus precisa do **ASP.NET Core 10 Hosting Bundle** no servidor. Instalá-lo **reinicia o IIS**: em servidor de site do SCCM, interrompe por instantes o management point e o distribution point, então faça em **janela de manutenção**.

Você não precisa instalar à mão: o `Instalar.cmd` (etapa 7) detecta que falta, avisa e pergunta se pode instalar o `prereq\dotnet-hosting-win.exe` do pacote e reiniciar o IIS (responda **S** na janela de manutenção). Para instalar à mão antes:

```powershell
Start-Process C:\Instalacao\AzulNexus\prereq\dotnet-hosting-win.exe -ArgumentList '/install','/quiet','/norestart' -Wait
iisreset
Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"   # deve dar True
```

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

### 7.1 Caminho rápido (duplo clique)

Na pasta `C:\Instalacao\AzulNexus`:

1. Duplo clique em **`Verificar-Ambiente.cmd`** (não altera nada). Na primeira vez ele cria o `deploy\install.json` e abre o Bloco de Notas: informe **`database.server`** e **`iis.hostName`** (veja 7.2), salve e feche. Cada item aparece como **OK**, **Atenção** ou **Bloqueio**, com o "Como resolver". Resolva os Bloqueios e repita.
2. Duplo clique em **`Instalar.cmd`**. Ele pede permissão de administrador, pergunta sobre o Hosting Bundle (se faltar) e instala.

No final aparecem o endereço (`https://nexus.suaempresa.local:8443`) e o **código de configuração** (uso único, vale 24 h). **Anote o código**: ele não vai para o arquivo de log. O código de saída `0` é sucesso, `10` é bloqueio de pré-requisito (nada foi alterado) e `20` é falha com desfazer.

### 7.2 Arquivo de respostas (`deploy\install.json`)

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

### 7.3 Pelo PowerShell (alternativa, com mais opções)

```powershell
cd C:\Instalacao\AzulNexus\deploy
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\Install-AzulNexus.ps1 -DetectOnly                 # só valida
.\Install-AzulNexus.ps1                             # instala
.\Install-AzulNexus.ps1 -AllowIisRestart            # idem, autorizando instalar o Hosting Bundle do pacote (reinicia o IIS)
.\Install-AzulNexus.ps1 -AllowSelfSigned            # teste com certificado autoassinado
```

Banco PostgreSQL: o script pede a senha do usuário e a guarda protegida (DPAPI), nunca em texto claro.

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

Baixe o novo pacote na página de Releases (etapa 1), descompacte numa pasta nova, copie o seu `deploy\\install.json` antigo para ela e dê duplo clique em `Instalar.cmd` (ou rode `Install-AzulNexus.ps1`). Ele faz backup do banco antes de migrar e restaura os binários anteriores se algo falhar. Nada de configuração ou dados é perdido.

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
