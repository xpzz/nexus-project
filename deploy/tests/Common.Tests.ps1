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
Assert-That 'SQL local usa conta virtual' { $local.Problems.Count -eq 0 -and $local.WebAccount -eq 'IIS APPPOOL\AzulNexus' -and $local.WorkerAccount -eq 'NT SERVICE\AzulNexus.Worker' }
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

# Portas e strings de conexão
Assert-That 'Portas do SCCM/WSUS são reservadas' { (Test-PortReserved 443) -and (Test-PortReserved 8530) -and (Test-PortReserved 8531) -and -not (Test-PortReserved 8443) }
$db = [pscustomobject]@{ server = 'SQL02'; name = 'AzulNexus'; port = 5432; username = 'nx' }
$cs = New-NexusConnectionString -Provider 'SqlServer' -Database $db
Assert-That 'String SQL Server usa segurança integrada e cifra' { $cs -like '*Integrated Security=true*' -and $cs -like '*Encrypt=True*' -and $cs -like '*TrustServerCertificate=False*' -and $cs -notlike '*Password*' }
$pgcs = New-NexusConnectionString -Provider 'PostgreSql' -Database $db
Assert-That 'String PostgreSQL nunca carrega senha' { $pgcs -like '*Host=SQL02*' -and $pgcs -notlike '*Password=*' }

# Arquivo resolvido
$ctx = [pscustomobject]@{
    InstallDir = 'C:\Program Files\Azul Nexus'; DataDir = 'D:\AzulNexus'; PublicUrl = 'https://nexus.azul.local:8443'; DemoMode = $false; Collection = $null
    Database = [pscustomobject]@{ Provider = 'SqlServer'; ConnectionString = $cs }
    Sccm = [pscustomobject]@{ SiteCode = 'AZ1'; SqlServer = 'SQL03'; Database = 'CM_AZ1'; TrustServerCertificate = $false }
    ActiveDirectory = [pscustomobject]@{ Domain = 'azul.local'; Server = $null; SearchBases = @(); UseLdaps = $false }
}
$resolved = ConvertTo-ResolvedInstallFile $ctx
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

# Falhas seguem o padrão do produto
$caught = $null
try { Stop-Install -ExitCode 10 -WhatHappened 'a' -Impact 'b' -HowToFix 'c' } catch { $caught = $_.Exception }
Assert-That 'Stop-Install carrega código e as três partes' { $caught.Data['ExitCode'] -eq 10 -and $caught.Message -like '*O que aconteceu*Impacto*Como resolver*' }

if ($script:Failures -gt 0) { Write-Host "$script:Failures falha(s)." -ForegroundColor Red; exit 1 }
Write-Host 'Todos os testes passaram.'
