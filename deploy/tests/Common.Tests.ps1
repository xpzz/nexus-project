# Testes das funções puras de deploy/lib/Common.ps1. Execute: pwsh -File deploy/tests/Common.Tests.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\Common.ps1')

$script:Failures = 0
function Assert-That {
    param([string]$Name, [scriptblock]$Condition)
    $ok = $false
    try { $ok = [bool](& $Condition) } catch { Write-Host "  exceção: $($_.Exception.Message)" }
    if ($ok) { Write-Host "OK   $Name" } else { Write-Host "FALHA $Name" -ForegroundColor Red; $script:Failures++ }
}

# Get-Setting / Test-IsAuto
$json = '{"a":{"b":"x","c":"  ","d":null,"e":"auto"},"n":5}' | ConvertFrom-Json
Assert-That 'Get-Setting lê caminho aninhado' { (Get-Setting $json 'a.b' 'd') -eq 'x' }
Assert-That 'Get-Setting usa padrão para vazio' { (Get-Setting $json 'a.c' 'd') -eq 'd' }
Assert-That 'Get-Setting usa padrão para nulo/ausente' { (Get-Setting $json 'a.d' 'd') -eq 'd' -and (Get-Setting $json 'x.y.z' 'd') -eq 'd' }
Assert-That 'Test-IsAuto reconhece auto' { (Test-IsAuto 'AUTO') -and (Test-IsAuto $null) -and -not (Test-IsAuto 'C:\x') }

# SQL local x remoto
Assert-That 'SQL local por nome do computador' { Test-LocalSqlServer 'SRV01\SCCM' 'SRV01' }
Assert-That 'SQL local por FQDN' { Test-LocalSqlServer 'srv01.azul.local' 'SRV01' }
Assert-That 'SQL local por localhost' { (Test-LocalSqlServer '(local)\X' 'SRV01') -and (Test-LocalSqlServer 'localhost,1433' 'SRV01') }
Assert-That 'SQL remoto' { -not (Test-LocalSqlServer 'SQL02\CM' 'SRV01') }
Assert-That 'Mesma instância ignora FQDN e maiúsculas' { Test-SameSqlInstance 'SQL02.azul.local\CM' 'sql02\cm' }
Assert-That 'Instâncias diferentes' { -not (Test-SameSqlInstance 'SQL02\CM' 'SQL02\NEXUS') }
Assert-That 'Instância padrão x nomeada' { -not (Test-SameSqlInstance 'SQL02' 'SQL02\NEXUS') }

# Pasta de dados
$disks = @(
    [pscustomobject]@{ DeviceID = 'C:'; FreeSpace = 80GB },
    [pscustomobject]@{ DeviceID = 'D:'; FreeSpace = 200GB },
    [pscustomobject]@{ DeviceID = 'E:'; FreeSpace = 20GB })
Assert-That 'Dados no maior volume fora do SO' { (Select-DataDirectory -Disks $disks -SystemDrive 'C:' -ProgramData 'C:\ProgramData') -eq 'D:\AzulNexus' }
Assert-That 'Sem outro volume usa ProgramData' { (Select-DataDirectory -Disks @($disks[0]) -SystemDrive 'C:' -ProgramData 'C:\ProgramData') -eq 'C:\ProgramData\AzulNexus' }

# Certificado
$now = Get-Date '2026-10-03'
function New-FakeCert($thumb, $subject, $issuer, $dns, $notAfter, $private = $true, $eku = @('1.3.6.1.5.5.7.3.1')) {
    [pscustomobject]@{
        Thumbprint = $thumb; Subject = $subject; Issuer = $issuer; HasPrivateKey = $private
        NotBefore = $now.AddYears(-1); NotAfter = $notAfter
        DnsNameList = @($dns | ForEach-Object { @{ Unicode = $_ } })
        EnhancedKeyUsageList = @($eku | ForEach-Object { [pscustomobject]@{ ObjectId = $_ } })
    }
}
$host1 = 'nexus.azul.local'
$ca = 'CN=Azul AC'
$certs = @(
    (New-FakeCert 'SELF' "CN=$host1" "CN=$host1" @($host1) $now.AddYears(5)),
    (New-FakeCert 'CA-OLD' "CN=$host1" $ca @($host1) $now.AddDays(10)),
    (New-FakeCert 'CA-OK' "CN=$host1" $ca @($host1) $now.AddYears(1)),
    (New-FakeCert 'CA-LONG' "CN=$host1" $ca @($host1) $now.AddYears(2)),
    (New-FakeCert 'OTHER' 'CN=outro.azul.local' $ca @('outro.azul.local') $now.AddYears(3)),
    (New-FakeCert 'NOKEY' "CN=$host1" $ca @($host1) $now.AddYears(3) $false),
    (New-FakeCert 'CLIENTAUTH' "CN=$host1" $ca @($host1) $now.AddYears(3) $true @('1.3.6.1.5.5.7.3.2')),
    (New-FakeCert 'EXPIRED' "CN=$host1" $ca @($host1) $now.AddDays(-1)))
Assert-That 'Escolhe certificado de AC com a maior validade' { (Select-BestCertificate -Certificates $certs -HostName $host1 -Now $now).Thumbprint -eq 'CA-LONG' }
Assert-That 'Prefere AC a autoassinado' { (Select-BestCertificate -Certificates @($certs[0], $certs[3]) -HostName $host1 -Now $now).Thumbprint -eq 'CA-LONG' }
Assert-That 'Aceita autoassinado se for o único adequado' { (Select-BestCertificate -Certificates @($certs[0]) -HostName $host1 -Now $now).Thumbprint -eq 'SELF' }
Assert-That 'Evita o que vence em menos de 30 dias' { (Select-BestCertificate -Certificates @($certs[1], $certs[2]) -HostName $host1 -Now $now).Thumbprint -eq 'CA-OK' }
Assert-That 'Usa o que vence logo se não houver outro' { (Select-BestCertificate -Certificates @($certs[1]) -HostName $host1 -Now $now).Thumbprint -eq 'CA-OLD' }
Assert-That 'Descarta sem chave, sem server auth, expirado e de outro nome' { $null -eq (Select-BestCertificate -Certificates @($certs[4], $certs[5], $certs[6], $certs[7]) -HostName $host1 -Now $now) }
$wild = New-FakeCert 'WILD' 'CN=*.azul.local' $ca @('*.azul.local') $now.AddYears(1)
Assert-That 'Curinga cobre um nível' { (Select-BestCertificate -Certificates @($wild) -HostName $host1 -Now $now).Thumbprint -eq 'WILD' }
Assert-That 'Curinga não cobre dois níveis' { $null -eq (Select-BestCertificate -Certificates @($wild) -HostName 'a.b.azul.local' -Now $now) }

# Identidades
$local = Resolve-ServiceAccounts -PoolName 'AzulNexus' -Provider 'SqlServer' -NexusDbServer 'localhost' -SccmSqlServer 'localhost' -SccmLive $true
Assert-That 'SQL local usa conta virtual' { $local.Problems.Count -eq 0 -and $local.WebAccount -eq 'NT SERVICE\AzulNexus.Web' -and $local.WorkerAccount -eq 'NT SERVICE\AzulNexus.Worker' }
$remote = Resolve-ServiceAccounts -PoolName 'AzulNexus' -Provider 'SqlServer' -NexusDbServer 'SQL02' -SccmSqlServer 'SQL03' -SccmLive $true
Assert-That 'SQL remoto sem gMSA bloqueia web e worker' { $remote.Problems.Count -eq 2 }
$remoteOk = Resolve-ServiceAccounts -WebGmsa 'AZUL\nexus-web$' -WorkerGmsa 'AZUL\nexus-wrk$' -PoolName 'AzulNexus' -Provider 'SqlServer' -NexusDbServer 'SQL02' -SccmSqlServer 'SQL03' -SccmLive $true
Assert-That 'SQL remoto com gMSA aceita' { $remoteOk.Problems.Count -eq 0 -and $remoteOk.WebAccount -eq 'AZUL\nexus-web$' }
$sccmRemoteOnly = Resolve-ServiceAccounts -PoolName 'AzulNexus' -Provider 'SqlServer' -NexusDbServer 'localhost' -SccmSqlServer 'SQL03' -SccmLive $true
Assert-That 'SCCM remoto exige gMSA só no Worker' { $sccmRemoteOnly.Problems.Count -eq 1 -and $sccmRemoteOnly.Problems[0] -like '*workerGmsa*' }
$pg = Resolve-ServiceAccounts -PoolName 'AzulNexus' -Provider 'PostgreSql' -NexusDbServer 'pg01' -SccmSqlServer $null -SccmLive $false
Assert-That 'PostgreSQL remoto (senha) não exige gMSA' { $pg.Problems.Count -eq 0 }

$shared = Resolve-ServiceAccounts -Account 'CORP\svc.sccm' -PoolName 'AzulNexus' -Provider 'SqlServer' -NexusDbServer 'SQL02' -SccmSqlServer 'SQL03' -SccmLive $true
Assert-That 'Conta única: site e Worker usam a mesma conta, sem exigir gMSA (SQL remoto)' { $shared.Problems.Count -eq 0 -and $shared.WebAccount -eq 'CORP\svc.sccm' -and $shared.WorkerAccount -eq 'CORP\svc.sccm' -and $shared.Mode -eq 'SharedAccount' }
Assert-That 'Sem conta única o padrão continua valendo' { $local.Mode -eq 'Default' }
Assert-That 'Conta sem domínio recebe o domínio padrão' { (Resolve-AccountName 'svc.sccm' 'CORP') -eq 'CORP\svc.sccm' }
Assert-That 'DOMINIO\conta e UPN são mantidos' { (Resolve-AccountName 'AZUL\svc.sccm' 'CORP') -eq 'AZUL\svc.sccm' -and (Resolve-AccountName 'svc.sccm@azul.local' 'CORP') -eq 'svc.sccm@azul.local' }
Assert-That 'Conta vazia vira nulo' { $null -eq (Resolve-AccountName '  ' 'CORP') }

# Hospedagem do site: serviço (padrão) x IIS
$svcDefault = Resolve-ServiceAccounts -PoolName 'AzulNexus' -Provider 'SqlServer' -NexusDbServer 'localhost' -SccmSqlServer 'localhost' -SccmLive $true
Assert-That 'Padrão é serviço: conta do site é NT SERVICE\AzulNexus.Web (sem IIS APPPOOL)' { $svcDefault.WebAccount -eq 'NT SERVICE\AzulNexus.Web' }
$iisDefault = Resolve-ServiceAccounts -WebHosting 'iis' -PoolName 'AzulNexus' -Provider 'SqlServer' -NexusDbServer 'localhost' -SccmSqlServer 'localhost' -SccmLive $true
Assert-That 'Modo iis mantém IIS APPPOOL\AzulNexus' { $iisDefault.WebAccount -eq 'IIS APPPOOL\AzulNexus' }
$sharedSvc = Resolve-ServiceAccounts -Account 'AZUL_CORP\svc.sccm' -WebHosting 'service' -PoolName 'AzulNexus' -Provider 'SqlServer' -NexusDbServer 'SQL02' -SccmSqlServer 'SQL03' -SccmLive $true
Assert-That 'Conta única no modo serviço: site e Worker na mesma conta' { $sharedSvc.WebAccount -eq 'AZUL_CORP\svc.sccm' -and $sharedSvc.WorkerAccount -eq 'AZUL_CORP\svc.sccm' }

# Portas e strings de conexão
Assert-That 'Portas do SCCM/WSUS são reservadas' { (Test-PortReserved 443) -and (Test-PortReserved 8530) -and (Test-PortReserved 8531) -and -not (Test-PortReserved 8443) }
$db = [pscustomobject]@{ server = 'SQL02'; name = 'AzulNexus'; port = 5432; username = 'nx' }
$cs = New-NexusConnectionString -Provider 'SqlServer' -Database $db
Assert-That 'String SQL Server usa segurança integrada e cifra' { $cs -like '*Integrated Security=true*' -and $cs -like '*Encrypt=True*' -and $cs -like '*TrustServerCertificate=False*' -and $cs -notlike '*Password*' }
$pgcs = New-NexusConnectionString -Provider 'PostgreSql' -Database $db
Assert-That 'String PostgreSQL nunca carrega senha' { $pgcs -like '*Host=SQL02*' -and $pgcs -notlike '*Password=*' }

# Arquivo resolvido
$ctx = [pscustomobject]@{
    InstallDir = 'C:\Program Files\Azul Nexus'; DataDir = 'D:\AzulNexus'; PublicUrl = 'https://nexus.azul.local:8443'; DemoMode = $false; Collection = $null; Hosting = 'service'; Iis = [pscustomobject]@{ Port = 8443; Thumbprint = 'AABBCC' }
    Database = [pscustomobject]@{ Provider = 'SqlServer'; ConnectionString = $cs }
    Sccm = [pscustomobject]@{ SiteCode = 'AZ1'; SqlServer = 'SQL03'; Database = 'CM_AZ1'; TrustServerCertificate = $false }
    ActiveDirectory = [pscustomobject]@{ Domain = 'azul.local'; Server = $null; SearchBases = @(); UseLdaps = $false }
}
$resolved = ConvertTo-ResolvedInstallFile $ctx
Assert-That 'Arquivo resolvido leva hospedagem, porta e certificado do site' { $resolved.hosting -eq 'service' -and $resolved.httpsPort -eq 8443 -and $resolved.certificateThumbprint -eq 'AABBCC' }
Assert-That 'Arquivo resolvido não tem "auto" e define os modos' { (($resolved | ConvertTo-Json -Depth 6) -notmatch '"auto"') -and $resolved.sccm.mode -eq 'Live' -and $resolved.activeDirectory.mode -eq 'Live' }
$ctx.Sccm.SqlServer = $null
Assert-That 'Sem SQL do SCCM o modo é Disabled (não configurado)' { (ConvertTo-ResolvedInstallFile $ctx).sccm.mode -eq 'Disabled' }

# Argumentos do nexusctl (regressão: --account ausente quando a lista era montada com + solto)
$args1 = New-AccountArguments -Command 'db-grant' -Accounts @('CORP\svc.sccm', 'CORP\svc.sccm')
Assert-That 'Conta única gera um único --account com valor' { $args1.Count -eq 3 -and $args1[0] -eq 'db-grant' -and $args1[1] -eq '--account' -and $args1[2] -eq 'CORP\svc.sccm' }
$args2 = New-AccountArguments -Command 'db-grant-script' -Accounts @('IIS APPPOOL\AzulNexus', 'NT SERVICE\AzulNexus.Worker') -Extra @('--output', 'C:\x.sql')
Assert-That 'Duas contas repetem --account e mantêm os extras' { ($args2 -join '|') -eq 'db-grant-script|--account|IIS APPPOOL\AzulNexus|--account|NT SERVICE\AzulNexus.Worker|--output|C:\x.sql' }
Assert-That 'Resultado é uma lista de strings (não é desmembrado)' { $args1 -is [string[]] }

# Escolha do banco do Nexus x instância do SCCM
function Get-DbCheck($name, $allow, $server = 'sccmdb01.azul.corp', $provider = 'SqlServer') {
    (Test-NexusDatabaseChoice -Provider $provider -Server $server -Name $name -SccmServer 'sccmdb01.azul.corp' -SccmDatabase 'CM_AZ1' -AllowSccmInstance $allow)[0]
}
Assert-That 'Mesma instância do SCCM sem liberação: bloqueia e mostra o JSON pronto' { $c = Get-DbCheck 'AzulNexus' $false; $c.Status -eq 'Bloqueio' -and $c.Fix -like '*"allowSccmInstance": true*' -and $c.Fix -like '*sccmdb01.azul.corp*' }
Assert-That 'Mesma instância liberada: só Atenção (licença e carga)' { $c = Get-DbCheck 'AzulNexus' $true; $c.Status -eq 'Atenção' -and $c.Message -like '*licenciamento*' }
Assert-That 'Banco do site (CM_xxx) é bloqueado mesmo com liberação' { (Get-DbCheck 'CM_AZ1' $true).Status -eq 'Bloqueio' -and (Get-DbCheck 'CM_ZZZ' $true).Status -eq 'Bloqueio' }
Assert-That 'Outra instância SQL: OK sem exigir liberação' { (Get-DbCheck 'AzulNexus' $false 'sql-nexus01').Status -eq 'OK' }
Assert-That 'Instância nomeada diferente na mesma máquina: OK' { (Get-DbCheck 'AzulNexus' $false 'sccmdb01.azul.corp\NEXUS').Status -eq 'OK' }
Assert-That 'PostgreSQL não é afetado' { (Get-DbCheck 'azulnexus' $false 'sccmdb01.azul.corp' 'PostgreSql').Status -eq 'OK' }
Assert-That 'Sem servidor: bloqueia' { (Test-NexusDatabaseChoice -Provider 'SqlServer' -Server '' -Name 'AzulNexus' -SccmServer 'x' -SccmDatabase 'CM_AZ1' -AllowSccmInstance $false)[0].Status -eq 'Bloqueio' }

# Invoke-Native: stderr não pode derrubar o script com $ErrorActionPreference = 'Stop' (regressão do Windows PowerShell 5.1)
$isWin = ($PSVersionTable.PSEdition -eq 'Desktop') -or $IsWindows
$native = if ($isWin) { @{ FilePath = 'cmd.exe'; Arguments = @('/c', 'echo mensagem-de-erro 1>&2 & exit 3') } } else { @{ FilePath = 'sh'; Arguments = @('-c', 'echo mensagem-de-erro >&2; exit 3') } }
$oldPreference = $ErrorActionPreference; $ErrorActionPreference = 'Stop'
$nativeResult = $null; $nativeThrew = $false
try { $nativeResult = Invoke-Native @native -AllowFailure } catch { $nativeThrew = $true }
$ErrorActionPreference = $oldPreference
Assert-That 'Invoke-Native -AllowFailure devolve o código e o stderr sem lançar exceção' { -not $nativeThrew -and $nativeResult.ExitCode -eq 3 -and ($nativeResult.Output -join ' ') -like '*mensagem-de-erro*' }
$failed = $null
try { Invoke-Native @native | Out-Null } catch { $failed = $_.Exception }
Assert-That 'Invoke-Native sem -AllowFailure falha com o padrão do produto e código 20' { $failed -and $failed.Data['ExitCode'] -eq 20 -and $failed.Message -like '*mensagem-de-erro*' -and $failed.Message -like '*Como resolver*' }
$okNative = if ($isWin) { Invoke-Native -FilePath 'cmd.exe' -Arguments @('/c', 'echo ok') } else { Invoke-Native -FilePath 'sh' -Arguments @('-c', 'echo ok') }
Assert-That 'Invoke-Native com sucesso devolve a saída' { $okNative.ExitCode -eq 0 -and ($okNative.Output -join '') -like '*ok*' }
Assert-That 'ErrorActionPreference é restaurado depois da chamada' { $ErrorActionPreference -eq 'Stop' }

# Interpretação da sondagem do /healthz
$okProbe = Resolve-HealthProbe -StatusCode 200 -Body '{"status":"ok","version":"0.1.0.0","setupMode":true}' -ErrorText ''
Assert-That 'healthz 200 ok é sucesso' { $okProbe.Ok }
$dbProbe = Resolve-HealthProbe -StatusCode 503 -Body '{"status":"database-unreachable","version":"0.1.0.0"}' -ErrorText ''
Assert-That 'healthz 503 database-unreachable aponta o banco, não "site não respondeu"' { -not $dbProbe.Ok -and $dbProbe.Summary -like '*banco do Nexus*' -and $dbProbe.HowToFix -like '*svc.sccm*' }
$migProbe = Resolve-HealthProbe -StatusCode 503 -Body '{"status":"migrations-pending"}' -ErrorText ''
Assert-That 'migrations-pending manda rodar o migrate' { -not $migProbe.Ok -and $migProbe.HowToFix -like '*nexusctl migrate*' }
$cfgProbe = Resolve-HealthProbe -StatusCode 503 -Body '{"status":"unconfigured"}' -ErrorText ''
Assert-That 'unconfigured aponta a configuração' { -not $cfgProbe.Ok -and $cfgProbe.Summary -like '*configuração*' }
$p500 = Resolve-HealthProbe -StatusCode 503 -Body '' -ErrorText ''
Assert-That 'HTTP 5xx sem JSON diz que o aplicativo não iniciou e manda ver o pool' { -not $p500.Ok -and $p500.Summary -like '*HTTP 503*' -and $p500.HowToFix -like '*pool*' }
$p404 = Resolve-HealthProbe -StatusCode 404 -Body '' -ErrorText ''
Assert-That 'HTTP 404 aponta o nome/binding' { -not $p404.Ok -and $p404.Summary -like '*404*' -and $p404.HowToFix -like '*iis.hostName*' }
$pErr = Resolve-HealthProbe -StatusCode $null -Body '' -ErrorText 'The request was aborted: Could not create SSL/TLS secure channel.'
Assert-That 'Erro de conexão mostra o erro real e menciona TLS 1.2' { -not $pErr.Ok -and $pErr.Summary -like '*Could not create SSL/TLS*' -and $pErr.HowToFix -like '*TLS 1.2*' }
Assert-That 'Código de saída 30 existe para instalado sem saúde' { $script:ExitCodes.InstalledNotHealthy -eq 30 }

# Logon da conta (LogonUser): explicações
$l1385b = Resolve-LogonFailure -Code 1385 -LogonType 'Batch' -Account 'AZUL_CORP\svc.sccm'
Assert-That 'Erro 1385 em lote: aponta o direito de lote, a GPO e o Negar logon' { $l1385b.Definitive -and $l1385b.Summary -like '*trabalho em lote*' -and $l1385b.HowToFix -like '*Log on as a batch job*' -and $l1385b.HowToFix -like '*GPO*' -and $l1385b.HowToFix -like '*Negar logon como um trabalho em lote*' }
$l1385s = Resolve-LogonFailure -Code 1385 -LogonType 'Service' -Account 'AZUL_CORP\svc.sccm'
Assert-That 'Erro 1385 como serviço: aponta o direito de serviço' { $l1385s.Summary -like '*como serviço*' -and $l1385s.HowToFix -like '*Log on as a service*' }
Assert-That 'Senha errada, expirada, desabilitada e bloqueada são definitivas e distintas' { (Resolve-LogonFailure 1326 'Batch' 'a').Summary -like '*incorretos*' -and (Resolve-LogonFailure 1330 'Batch' 'a').Summary -like '*expirou*' -and (Resolve-LogonFailure 1331 'Batch' 'a').Summary -like '*desabilitada*' -and (Resolve-LogonFailure 1909 'Batch' 'a').Summary -like '*bloqueada*' }
Assert-That 'Erro desconhecido não é definitivo (não bloqueia a instalação)' { -not (Resolve-LogonFailure 5 'Batch' 'a').Definitive }

# Falhas seguem o padrão do produto
$caught = $null
try { Stop-Install -ExitCode 10 -WhatHappened 'a' -Impact 'b' -HowToFix 'c' } catch { $caught = $_.Exception }
Assert-That 'Stop-Install carrega código e as três partes' { $caught.Data['ExitCode'] -eq 10 -and $caught.Message -like '*O que aconteceu*Impacto*Como resolver*' }

if ($script:Failures -gt 0) { Write-Host "$script:Failures falha(s)." -ForegroundColor Red; exit 1 }
Write-Host 'Todos os testes passaram.'
